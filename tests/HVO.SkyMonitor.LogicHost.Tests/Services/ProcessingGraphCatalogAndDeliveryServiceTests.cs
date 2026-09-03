using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphCatalogAndDeliveryServiceTests
{
    [TestMethod]
    public async Task CatalogLifecycleIsAuthorizedIdempotentScopedAndResolvable()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        using var telemetry = new ProcessingGraphCatalogTelemetry(clock);
        var service = CreateCatalog(context, clock, telemetry);
        var definition = CreateDefinition("catalog-lifecycle", "1");

        var denied = await service.CreateRevisionAsync(
            definition, "operator", false, CancellationToken.None).ConfigureAwait(false);
        var created = await service.CreateRevisionAsync(
            definition, "editor", true, CancellationToken.None).ConfigureAwait(false);
        var unchanged = await service.CreateRevisionAsync(
            definition, "editor", true, CancellationToken.None).ConfigureAwait(false);
        var conflict = await service.CreateRevisionAsync(
            CreateDefinition("catalog-lifecycle", "1", nodeId: "PreviewChanged"),
            "editor", true, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphMutationOutcome.NotFoundOrDenied, denied.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Applied, created.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Unchanged, unchanged.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Conflict, conflict.Outcome);
        Assert.AreEqual(created.Value!.Id, unchanged.Value!.Id);
        var revisions = await service.ListRevisionsAsync(100, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, revisions);
        var revisionView = revisions.Single();
        Assert.AreEqual(created.Value.Id, revisionView.Id);
        Assert.AreEqual(definition.Name, revisionView.Name);
        Assert.AreEqual(definition.Revision, revisionView.Revision);
        Assert.AreEqual(CentralProcessingGraphLifecycle.Draft, revisionView.Lifecycle);
        Assert.AreEqual(created.Value.DefinitionIdentitySha256, revisionView.DefinitionIdentitySha256);
        Assert.AreEqual(created.Value.PortablePlanIdentitySha256, revisionView.PortablePlanIdentitySha256);
        Assert.AreEqual(created.Value.EdgePlanIdentitySha256, revisionView.EdgePlanIdentitySha256);
        Assert.AreEqual(created.Value.CentralPlanIdentitySha256, revisionView.CentralPlanIdentitySha256);
        Assert.AreEqual(definition.Name, revisionView.Definition.GetProperty("name").GetString());
        Assert.AreEqual(now, revisionView.CreatedAtUtc);
        Assert.IsNull(revisionView.PublishedAtUtc);
        Assert.IsNull(revisionView.RetiredAtUtc);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await service.ListRevisionsAsync(0, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var published = await service.PublishRevisionAsync(
            created.Value.Id, "editor", true, CancellationToken.None).ConfigureAwait(false);
        var publishedAgain = await service.PublishRevisionAsync(
            created.Value.Id, "editor", true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Applied, published.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Unchanged, publishedAgain.Outcome);

        var request = new CentralProcessingGraphAssignmentRequest(
            created.Value.Id,
            CentralProcessingGraphTargetHost.Central,
            CentralProcessingGraphAssignmentScope.GlobalDefault,
            null,
            null,
            now,
            null,
            "canonical-default");
        var assigned = await service.AssignAsync(
            request, "editor", true, null, CancellationToken.None).ConfigureAwait(false);
        var duplicate = await service.AssignAsync(
            request, "editor", true, null, CancellationToken.None).ConfigureAwait(false);
        var assignmentConflict = await service.AssignAsync(
            request with { ReasonCode = "different-decision" },
            "editor", true, null, CancellationToken.None).ConfigureAwait(false);
        var backdated = await service.AssignAsync(
            request with { EffectiveFromUtc = now.AddTicks(-1) },
            "editor", true, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Applied, assigned.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Unchanged, duplicate.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Conflict, assignmentConflict.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Invalid, backdated.Outcome);
        var assignmentView = assigned.Value!;
        Assert.AreEqual(request.RevisionId, assignmentView.RevisionId);
        Assert.AreEqual(request.TargetHost, assignmentView.TargetHost);
        Assert.AreEqual(request.Scope, assignmentView.Scope);
        Assert.AreEqual(request.ObservatoryId, assignmentView.ObservatoryId);
        Assert.AreEqual(request.LogicalCameraId, assignmentView.LogicalCameraId);
        Assert.AreEqual(request.EffectiveFromUtc, assignmentView.EffectiveFromUtc);
        Assert.AreEqual(request.EffectiveUntilUtc, assignmentView.EffectiveUntilUtc);
        Assert.AreEqual(now, assignmentView.CreatedAtUtc);
        Assert.AreEqual(request.ReasonCode, assignmentView.ReasonCode);
        Assert.AreEqual(created.Value.Id, assignmentView.Revision.Id);

        var observatoryId = Guid.NewGuid();
        var resolved = await service.ResolveForUserAsync(
            CentralProcessingGraphTargetHost.Central,
            observatoryId,
            null,
            now,
            "editor",
            true,
            null,
            CancellationToken.None).ConfigureAwait(false);
        var deniedResolution = await service.ResolveForUserAsync(
            CentralProcessingGraphTargetHost.Central,
            observatoryId,
            null,
            now,
            "operator",
            false,
            null,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(assigned.Value!.Id, resolved!.Id);
        Assert.IsNull(deniedResolution);

        var retired = await service.RetireRevisionAsync(
            created.Value.Id, "editor", "superseded", true, CancellationToken.None).ConfigureAwait(false);
        var retiredAgain = await service.RetireRevisionAsync(
            created.Value.Id, "editor", "superseded", true, CancellationToken.None).ConfigureAwait(false);
        var republish = await service.PublishRevisionAsync(
            created.Value.Id, "editor", true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Applied, retired.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Unchanged, retiredAgain.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Conflict, republish.Outcome);
    }

    [TestMethod]
    public void CatalogEnumsRequireDefinedCaseSensitiveStringValues()
    {
        Assert.AreEqual(
            CentralProcessingGraphLifecycle.Published,
            JsonSerializer.Deserialize<CentralProcessingGraphLifecycle>("\"Published\""));
        Assert.AreEqual(
            "\"LogicalCamera\"",
            JsonSerializer.Serialize(CentralProcessingGraphAssignmentScope.LogicalCamera));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<CentralProcessingGraphLifecycle>("\"published\""));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<CentralProcessingGraphLifecycle>("1"));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<CentralProcessingGraphLifecycle>("\"Unknown\""));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Serialize((CentralProcessingGraphLifecycle)int.MaxValue));
    }

    [TestMethod]
    public void DeliveryTransitionsRequireExactOrderedLifecycleEvidence()
    {
        var now = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        var definitionIdentity = new string('A', 64);
        var sharedIdentity = new string('B', 64);
        var localRevision = new string('C', 64);
        var localPlan = new string('D', 64);
        var revision = new CentralProcessingGraphRevision
        {
            DefinitionIdentitySha256 = definitionIdentity,
            EdgePlanIdentitySha256 = sharedIdentity
        };
        var proposal = new CentralProcessingGraphDeliveryProposal
        {
            Revision = revision,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10)
        };
        var accepted = new CentralProcessingGraphDeliveryFact
        {
            Kind = nameof(ProcessingGraphDeliveryFactKind.Accepted),
            OccurredAtUtc = now.AddMinutes(1),
            LocalRevisionId = localRevision,
            DefinitionIdentitySha256 = definitionIdentity,
            SharedPlanIdentitySha256 = sharedIdentity,
            LocalPlanIdentitySha256 = localPlan
        };
        var activated = new CentralProcessingGraphDeliveryFact
        {
            Kind = nameof(ProcessingGraphDeliveryFactKind.Activated),
            OccurredAtUtc = now.AddMinutes(2)
        };
        var rolledBack = new CentralProcessingGraphDeliveryFact
        {
            Kind = nameof(ProcessingGraphDeliveryFactKind.RolledBack),
            OccurredAtUtc = now.AddMinutes(3)
        };

        ProcessingGraphDeliveryFactV1 Fact(
            ProcessingGraphDeliveryFactKind kind,
            DateTimeOffset? occurredAtUtc = null,
            string? reasonCode = null,
            string? localRevisionId = null,
            string? definition = null,
            string? shared = null,
            string? plan = null)
            => new(
                ProcessingGraphDeliverySchemaVersions.Current,
                Guid.NewGuid(),
                proposal.Id,
                kind,
                occurredAtUtc ?? now.AddMinutes(4),
                localRevisionId ?? localRevision,
                definition ?? definitionIdentity,
                shared ?? sharedIdentity,
                plan ?? localPlan,
                reasonCode);

        void Accepts(
            ProcessingGraphDeliveryFactV1 fact,
            ProcessingGraphDeliveryService.ProcessingGraphTransitionState state)
            => ProcessingGraphDeliveryService.ValidateTransition(proposal, fact, state);

        void Rejects(
            ProcessingGraphDeliveryFactV1 fact,
            ProcessingGraphDeliveryService.ProcessingGraphTransitionState state)
            => Assert.ThrowsExactly<InvalidOperationException>(() =>
                ProcessingGraphDeliveryService.ValidateTransition(proposal, fact, state));

        var empty = new ProcessingGraphDeliveryService.ProcessingGraphTransitionState(true, null, null);
        var acceptedState = new ProcessingGraphDeliveryService.ProcessingGraphTransitionState(true, accepted, null);
        Accepts(Fact(ProcessingGraphDeliveryFactKind.Accepted), empty);
        Accepts(Fact(ProcessingGraphDeliveryFactKind.Rejected, reasonCode: "unsupported"), empty);
        Accepts(Fact(
            ProcessingGraphDeliveryFactKind.Expired,
            proposal.ExpiresAtUtc,
            "proposal-expired"), empty);
        Accepts(Fact(ProcessingGraphDeliveryFactKind.Activated), acceptedState);
        Accepts(
            Fact(ProcessingGraphDeliveryFactKind.Activated, now.AddMinutes(4)),
            new(true, accepted, rolledBack));
        Accepts(
            Fact(ProcessingGraphDeliveryFactKind.RolledBack, now.AddMinutes(4)),
            new(true, accepted, activated));

        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted), new(false, null, null));
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted, now.AddTicks(-1)), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted, proposal.ExpiresAtUtc.AddTicks(1)), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted), acceptedState);
        proposal.Revision = null;
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted), empty);
        proposal.Revision = revision;
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted, definition: new string('0', 64)), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted, shared: new string('0', 64)), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted, localRevisionId: "bad"), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Accepted, plan: "bad"), empty);

        Rejects(Fact(
            ProcessingGraphDeliveryFactKind.Rejected,
            proposal.ExpiresAtUtc.AddTicks(1),
            "unsupported"), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Rejected, reasonCode: "unsupported"), acceptedState);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Rejected), empty);
        Rejects(Fact(
            ProcessingGraphDeliveryFactKind.Expired,
            proposal.ExpiresAtUtc.AddTicks(-1),
            "proposal-expired"), empty);
        Rejects(Fact(
            ProcessingGraphDeliveryFactKind.Expired,
            proposal.ExpiresAtUtc,
            "proposal-expired"), acceptedState);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Expired, proposal.ExpiresAtUtc), empty);

        Rejects(Fact(ProcessingGraphDeliveryFactKind.Activated), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Activated), new(true, accepted, activated));
        Rejects(
            Fact(ProcessingGraphDeliveryFactKind.Activated, rolledBack.OccurredAtUtc.AddTicks(-1)),
            new(true, accepted, rolledBack));
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Activated, now, localRevisionId: localRevision), acceptedState);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Activated, definition: new string('0', 64)), acceptedState);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Activated, shared: new string('0', 64)), acceptedState);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Activated, plan: new string('0', 64)), acceptedState);

        Rejects(Fact(ProcessingGraphDeliveryFactKind.RolledBack), empty);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.RolledBack), acceptedState);
        Rejects(Fact(ProcessingGraphDeliveryFactKind.RolledBack), new(true, accepted, rolledBack));
        Rejects(
            Fact(ProcessingGraphDeliveryFactKind.RolledBack, activated.OccurredAtUtc.AddTicks(-1)),
            new(true, accepted, activated));
        Rejects(
            Fact(ProcessingGraphDeliveryFactKind.RolledBack, definition: new string('0', 64)),
            new(true, accepted, activated));
        Rejects(Fact(ProcessingGraphDeliveryFactKind.Superseded), empty);
    }

    [TestMethod]
    public void CatalogSecretDetectionCoversNamesUrisTokensAndOpaqueValues()
    {
        var sensitiveNames = new[]
        {
            "password", "pwd", "secret", "token", "api_key", "private-key", "devicekey", "access-key",
            "credential", "authorization", "connection_string", "databasePassword", "clientSecret",
            "refreshToken", "serviceApiKey", "signingPrivateKey", "storageAccessKey", "clientCredential",
            "proxyAuthorization", "databaseConnectionString"
        };
        foreach (var name in sensitiveNames)
        {
            Assert.IsTrue(ProcessingGraphCatalogService.IsSensitiveName(name), name);
        }
        Assert.IsFalse(ProcessingGraphCatalogService.IsSensitiveName("endpoint"));

        Assert.IsFalse(ProcessingGraphCatalogService.ContainsSecretScalar(null, null));
        Assert.IsFalse(ProcessingGraphCatalogService.ContainsSecretScalar(" ", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("Bearer value", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("Basic value", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("-----BEGIN PRIVATE KEY-----", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("-----BEGIN RSA PRIVATE KEY-----", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("-----BEGIN EC PRIVATE KEY-----", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("-----BEGIN OPENSSH PRIVATE KEY-----", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("-----BEGIN CERTIFICATE-----", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("https://user:pass@example.test/path", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("https://example.test/?token=value", null));
        Assert.IsFalse(ProcessingGraphCatalogService.ContainsSecretScalar("https://example.test/path", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("host=x;password=y", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("eyJ12345678.abcdefgh.12345678", null));
        Assert.IsTrue(ProcessingGraphCatalogService.ContainsSecretScalar("AKIA0123456789012345", null));
        Assert.IsFalse(ProcessingGraphCatalogService.ContainsSecretScalar(new string('A', 64), "checksum"));

        Assert.IsFalse(ProcessingGraphCatalogService.QueryContainsSecret("?page=1&flag"));
        Assert.IsTrue(ProcessingGraphCatalogService.QueryContainsSecret("?key=value"));
        Assert.IsTrue(ProcessingGraphCatalogService.QueryContainsSecret("?sig=value"));
        Assert.IsTrue(ProcessingGraphCatalogService.QueryContainsSecret("?signature=value"));
        Assert.IsTrue(ProcessingGraphCatalogService.QueryContainsSecret("?api%5Fkey=value"));
        Assert.IsFalse(ProcessingGraphCatalogService.QueryContainsSecret("?%ZZ=value"));

        Assert.IsFalse(ProcessingGraphCatalogService.ConnectionStringContainsSecret("host=value"));
        Assert.IsFalse(ProcessingGraphCatalogService.ConnectionStringContainsSecret("host=value;invalid"));
        Assert.IsTrue(ProcessingGraphCatalogService.ConnectionStringContainsSecret("host=value;password=secret"));
        Assert.IsTrue(ProcessingGraphCatalogService.ConnectionStringContainsSecret("host=value;uid=user"));
        Assert.IsTrue(ProcessingGraphCatalogService.ConnectionStringContainsSecret("host=value;user id=user"));
        Assert.IsFalse(ProcessingGraphCatalogService.ConnectionStringContainsSecret("host=value;port=123"));

        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeJwt("one.two"));
        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeJwt("abc12345.abcdefgh.12345678"));
        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeJwt("eyJ.abcdefgh.12345678"));
        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeJwt("eyJ12345678.abc!efgh.12345678"));
        Assert.IsTrue(ProcessingGraphCatalogService.LooksLikeJwt("eyJ12345678.abc_def-.12345678"));

        foreach (var prefix in new[] { "AKIA", "ASIA", "ghp_", "github_pat_", "sk_live_" })
        {
            Assert.IsTrue(ProcessingGraphCatalogService.HasKnownTokenPrefix(string.Concat(prefix, "value")));
        }
        Assert.IsFalse(ProcessingGraphCatalogService.HasKnownTokenPrefix("ordinary"));

        Assert.IsFalse(ProcessingGraphCatalogService.IsOpaqueValueExemptName(null));
        Assert.IsFalse(ProcessingGraphCatalogService.IsOpaqueValueExemptName(" "));
        foreach (var name in new[]
        {
            "id", "deviceId", "device_id", "device-id", "hash", "checksum", "digest", "identity", "sha256",
            "contentHash", "payloadChecksum", "sourceDigest", "recipeIdentity", "contentSha256"
        })
        {
            Assert.IsTrue(ProcessingGraphCatalogService.IsOpaqueValueExemptName(name), name);
        }
        Assert.IsFalse(ProcessingGraphCatalogService.IsOpaqueValueExemptName("value"));

        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeOpaqueSecret("short"));
        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeOpaqueSecret(string.Concat(new string('a', 31), " ")));
        Assert.IsTrue(ProcessingGraphCatalogService.LooksLikeOpaqueSecret(new string('A', 48)));
        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeOpaqueSecret(new string('A', 32)));
        Assert.IsTrue(ProcessingGraphCatalogService.LooksLikeOpaqueSecret("0123456789abcdef0123456789abcdef"));
        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeOpaqueSecret(string.Concat(new string('a', 31), "!")));
        Assert.IsFalse(ProcessingGraphCatalogService.LooksLikeOpaqueSecret(new string('a', 32)));
        Assert.IsTrue(ProcessingGraphCatalogService.LooksLikeOpaqueSecret("abcdefghijklmnopqrstuvwxyzABCDEF"));
        Assert.IsFalse(ProcessingGraphCatalogService.HasHighHexEntropy(new string('0', 32)));
        Assert.IsTrue(ProcessingGraphCatalogService.HasHighHexEntropy("0123456789abcdef0123456789ABCDEF"));
    }

    [TestMethod]
    public async Task CatalogRejectsInvalidAuthorityScopeHostAndSecretMaterial()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        using var telemetry = new ProcessingGraphCatalogTelemetry(clock);
        var service = CreateCatalog(context, clock, telemetry);

        var hostIncompatible = await service.CreateRevisionAsync(
            CreateDefinition("host-incompatible", "1", hostApplicability: ["unsupported-host"]),
            "editor", true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Invalid, hostIncompatible.Outcome);
        Assert.AreEqual("host-incompatible", hostIncompatible.ReasonCode);

        var secretOptions = new[]
        {
            "{\"password\":\"value\"}",
            "{\"nested\":[{\"value\":\"Bearer credential\"}]}",
            "{\"value\":\"Basic credential\"}",
            "{\"value\":\"-----BEGIN PRIVATE KEY-----value\"}",
            "{\"value\":\"-----BEGIN RSA PRIVATE KEY-----value\"}",
            "{\"value\":\"-----BEGIN EC PRIVATE KEY-----value\"}",
            "{\"value\":\"-----BEGIN OPENSSH PRIVATE KEY-----value\"}",
            "{\"value\":\"-----BEGIN CERTIFICATE-----value\"}",
            "{\"endpoint\":\"https://user:password@example.invalid/path\"}",
            "{\"endpoint\":\"https://example.invalid/path?sig=value\"}",
            "{\"connection\":\"Server=database;User Id=operator\"}",
            "{\"value\":\"eyJabcdefgh.abcdefgh.abcdefgh\"}",
            "{\"value\":\"AKIAABCDEFGHIJKLMNOP\"}",
            "{\"value\":\"ASIAABCDEFGHIJKLMNOP\"}",
            "{\"value\":\"ghp_abcdefghijklmnopqrstuvwxyz012345\"}",
            "{\"value\":\"github_pat_abcdefghijklmnopqrstuvwxyz012345\"}",
            "{\"value\":\"sk_live_abcdefghijklmnopqrstuvwxyz012345\"}",
            "{\"value\":\"0123456789ABCDEF0123456789ABCDEF\"}",
            "{\"value\":\"abcdefghijklmnopqrstuvwxyzABCDEF0123456789\"}"
        };
        foreach (var (json, index) in secretOptions.Select((value, index) => (value, index)))
        {
            using var options = JsonDocument.Parse(json);
            var result = await service.CreateRevisionAsync(
                CreateDefinition($"secret-{index}", "1", options: options.RootElement.Clone()),
                "editor", true, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CentralProcessingGraphMutationOutcome.Invalid, result.Outcome, json);
            Assert.AreEqual("invalid-definition", result.ReasonCode, json);
        }

        using (var benignOptions = JsonDocument.Parse(
                   "{\"identitySha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"endpoint\":\"https://example.invalid/path\"}"))
        {
            var benign = await service.CreateRevisionAsync(
                CreateDefinition("benign-opaque", "1", options: benignOptions.RootElement.Clone()),
                "editor", true, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("host-incompatible", benign.ReasonCode);
        }

        var missingRevision = Guid.NewGuid();
        var validRequest = new CentralProcessingGraphAssignmentRequest(
            missingRevision,
            CentralProcessingGraphTargetHost.Central,
            CentralProcessingGraphAssignmentScope.GlobalDefault,
            null,
            null,
            now,
            null,
            "test");
        var missing = await service.AssignAsync(
            validRequest, "editor", true, null, CancellationToken.None).ConfigureAwait(false);
        var deniedGlobal = await service.AssignAsync(
            validRequest, "operator", false, null, CancellationToken.None).ConfigureAwait(false);
        var invalidScope = await service.AssignAsync(
            validRequest with
            {
                Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
                ObservatoryId = Guid.NewGuid(),
                LogicalCameraId = null
            },
            "editor", true, null, CancellationToken.None).ConfigureAwait(false);
        var invalidWindow = await service.AssignAsync(
            validRequest with { EffectiveUntilUtc = now },
            "editor", true, null, CancellationToken.None).ConfigureAwait(false);
        var scopedDenied = await service.AssignAsync(
            validRequest with
            {
                Scope = CentralProcessingGraphAssignmentScope.Observatory,
                ObservatoryId = Guid.NewGuid()
            },
            "operator", false, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Conflict, missing.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.NotFoundOrDenied, deniedGlobal.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Invalid, invalidScope.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Invalid, invalidWindow.Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.NotFoundOrDenied, scopedDenied.Outcome);

        Assert.AreEqual(CentralProcessingGraphMutationOutcome.NotFoundOrDenied,
            (await service.PublishRevisionAsync(
                missingRevision, "editor", true, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.NotFoundOrDenied,
            (await service.RetireRevisionAsync(
                missingRevision, "editor", "reason", true, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Invalid,
            (await service.RetireRevisionAsync(
                missingRevision, "editor", string.Empty, true, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CentralProcessingGraphMutationOutcome.NotFoundOrDenied,
            (await service.RetireRevisionAsync(
                missingRevision, "operator", "reason", false, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.IsNull(await service.ResolveForUserAsync(
            (CentralProcessingGraphTargetHost)int.MaxValue,
            Guid.NewGuid(),
            null,
            now,
            "editor",
            true,
            null,
            CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DeliveryProposalLifecycleConvergesAcrossReplayAndTerminalFacts()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        using var telemetry = new ProcessingGraphCatalogTelemetry(clock);
        var setup = await SeedDeliveryAsync(context, now).ConfigureAwait(false);
        var catalog = CreateCatalog(context, clock, telemetry);
        var service = new ProcessingGraphDeliveryService(
            context, catalog, clock, telemetry, NullLogger<ProcessingGraphDeliveryService>.Instance);
        var capabilities = ProcessingGraphAgentCapabilities.Create([BuiltInProcessingRecipes.EncodedPreview]);
        var request = new ProcessingGraphProposalPollRequestV1(
            ProcessingGraphDeliverySchemaVersions.Current,
            setup.Registration.DeviceId,
            null,
            null,
            null,
            capabilities);

        var proposed = await service.PullAsync(
            setup.Registration, request, CancellationToken.None).ConfigureAwait(false);
        var replayed = await service.PullAsync(
            setup.Registration, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphProposalPollDisposition.Proposed, proposed.Disposition);
        Assert.AreEqual(proposed.Proposal!.ProposalId, replayed.Proposal!.ProposalId);

        var localRevision = new string('A', 64);
        var localPlan = new string('B', 64);
        var acceptedFact = new ProcessingGraphDeliveryFactV1(
            ProcessingGraphDeliverySchemaVersions.Current,
            Guid.NewGuid(),
            proposed.Proposal.ProposalId,
            ProcessingGraphDeliveryFactKind.Accepted,
            now,
            localRevision,
            setup.Revision.DefinitionIdentitySha256,
            setup.Revision.EdgePlanIdentitySha256,
            localPlan,
            "accepted");
        var accepted = await service.AcknowledgeAsync(
            setup.Registration, acceptedFact, CancellationToken.None).ConfigureAwait(false);
        var duplicate = await service.AcknowledgeAsync(
            setup.Registration, acceptedFact, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphFactAcknowledgementDisposition.Recorded, accepted.Disposition);
        Assert.AreEqual(ProcessingGraphFactAcknowledgementDisposition.Duplicate, duplicate.Disposition);

        var staged = await service.PullAsync(
            setup.Registration, request, CancellationToken.None).ConfigureAwait(false);
        var current = await service.PullAsync(
            setup.Registration,
            request with
            {
                ActiveLocalRevisionId = localRevision,
                ActiveDefinitionIdentitySha256 = setup.Revision.DefinitionIdentitySha256,
                ActiveSharedPlanIdentitySha256 = setup.Revision.EdgePlanIdentitySha256
            },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphProposalPollDisposition.Staged, staged.Disposition);
        Assert.AreEqual(ProcessingGraphProposalPollDisposition.Current, current.Disposition);

        clock.UtcNow = now.AddSeconds(1);
        var activatedFact = acceptedFact with
        {
            FactId = Guid.NewGuid(),
            Kind = ProcessingGraphDeliveryFactKind.Activated,
            OccurredAtUtc = clock.UtcNow,
            ReasonCode = "activated"
        };
        var activated = await service.AcknowledgeAsync(
            setup.Registration, activatedFact, CancellationToken.None).ConfigureAwait(false);
        clock.UtcNow = now.AddSeconds(2);
        var rollbackFact = activatedFact with
        {
            FactId = Guid.NewGuid(),
            Kind = ProcessingGraphDeliveryFactKind.RolledBack,
            OccurredAtUtc = clock.UtcNow,
            ReasonCode = "rollback"
        };
        var rolledBack = await service.AcknowledgeAsync(
            setup.Registration, rollbackFact, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphFactAcknowledgementDisposition.Recorded, activated.Disposition);
        Assert.AreEqual(ProcessingGraphFactAcknowledgementDisposition.Recorded, rolledBack.Disposition);

        var alternateCapabilities = ProcessingGraphAgentCapabilities.Create(
            [BuiltInProcessingRecipes.EncodedPreview], ["alternate"]);
        var alternateRequest = request with { Capabilities = alternateCapabilities };
        var alternate = await service.PullAsync(
            setup.Registration, alternateRequest, CancellationToken.None).ConfigureAwait(false);
        var prematureActivation = activatedFact with
        {
            FactId = Guid.NewGuid(),
            ProposalId = alternate.Proposal!.ProposalId,
            OccurredAtUtc = clock.UtcNow
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.AcknowledgeAsync(
                setup.Registration, prematureActivation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        var rejectedFact = prematureActivation with
        {
            FactId = Guid.NewGuid(),
            Kind = ProcessingGraphDeliveryFactKind.Rejected,
            ReasonCode = "unsupported"
        };
        Assert.AreEqual(
            ProcessingGraphFactAcknowledgementDisposition.Recorded,
            (await service.AcknowledgeAsync(
                setup.Registration, rejectedFact, CancellationToken.None).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(
            ProcessingGraphProposalPollDisposition.Incompatible,
            (await service.PullAsync(
                setup.Registration, alternateRequest, CancellationToken.None).ConfigureAwait(false)).Disposition);

        var expiringRequest = request with
        {
            Capabilities = ProcessingGraphAgentCapabilities.Create(
                [BuiltInProcessingRecipes.EncodedPreview], ["expiring"])
        };
        var expiring = await service.PullAsync(
            setup.Registration, expiringRequest, CancellationToken.None).ConfigureAwait(false);
        clock.UtcNow = now.AddHours(2);
        context.ChangeTracker.Clear();
        _ = await service.PullAsync(setup.Registration, expiringRequest, CancellationToken.None).ConfigureAwait(false);
        var expiredFact = rejectedFact with
        {
            FactId = Guid.NewGuid(),
            ProposalId = expiring.Proposal!.ProposalId,
            Kind = ProcessingGraphDeliveryFactKind.Expired,
            OccurredAtUtc = clock.UtcNow,
            ReasonCode = "agent-expired"
        };
        Assert.AreEqual(
            ProcessingGraphFactAcknowledgementDisposition.Duplicate,
            (await service.AcknowledgeAsync(
                setup.Registration, expiredFact, CancellationToken.None).ConfigureAwait(false)).Disposition);

        var supersededRequest = request with
        {
            Capabilities = ProcessingGraphAgentCapabilities.Create(
                [BuiltInProcessingRecipes.EncodedPreview], ["superseded"])
        };
        var supersededProposal = await service.PullAsync(
            setup.Registration, supersededRequest, CancellationToken.None).ConfigureAwait(false);
        clock.UtcNow = now.AddHours(2).AddMinutes(45);
        context.ChangeTracker.Clear();
        var noAssignment = await service.PullAsync(
            setup.Registration, supersededRequest, CancellationToken.None).ConfigureAwait(false);
        var supersededFact = acceptedFact with
        {
            FactId = Guid.NewGuid(),
            ProposalId = supersededProposal.Proposal!.ProposalId,
            OccurredAtUtc = clock.UtcNow
        };
        Assert.AreEqual(ProcessingGraphProposalPollDisposition.NoAssignment, noAssignment.Disposition);
        Assert.AreEqual(
            ProcessingGraphFactAcknowledgementDisposition.Superseded,
            (await service.AcknowledgeAsync(
                setup.Registration, supersededFact, CancellationToken.None).ConfigureAwait(false)).Disposition);
    }

    [TestMethod]
    public async Task DeliveryRejectsInvalidPollsFactsAndUnavailableInstallations()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        using var telemetry = new ProcessingGraphCatalogTelemetry(clock);
        var catalog = CreateCatalog(context, clock, telemetry);
        var service = new ProcessingGraphDeliveryService(
            context, catalog, clock, telemetry, NullLogger<ProcessingGraphDeliveryService>.Instance);
        var registration = new DeviceRegistration
        {
            DeviceId = "agent",
            ObservatoryId = Guid.NewGuid(),
            IssuedAtUtc = now
        };
        context.DeviceRegistrations.Add(registration);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var capabilities = ProcessingGraphAgentCapabilities.Create([BuiltInProcessingRecipes.EncodedPreview]);
        var request = new ProcessingGraphProposalPollRequestV1(
            ProcessingGraphDeliverySchemaVersions.Current, registration.DeviceId, null, null, null, capabilities);

        Assert.AreEqual(
            ProcessingGraphProposalPollDisposition.NoAssignment,
            (await service.PullAsync(registration, request, CancellationToken.None).ConfigureAwait(false)).Disposition);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.PullAsync(
                registration, request with { AgentId = "different" }, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.PullAsync(
                registration,
                request with
                {
                    Capabilities = capabilities with { IdentitySha256 = new string('0', 64) }
                },
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var invalidFacts = new[]
        {
            new ProcessingGraphDeliveryFactV1(
                "invalid", Guid.NewGuid(), Guid.NewGuid(), ProcessingGraphDeliveryFactKind.Accepted, now),
            new ProcessingGraphDeliveryFactV1(
                ProcessingGraphDeliverySchemaVersions.Current, Guid.Empty, Guid.NewGuid(),
                ProcessingGraphDeliveryFactKind.Accepted, now),
            new ProcessingGraphDeliveryFactV1(
                ProcessingGraphDeliverySchemaVersions.Current, Guid.NewGuid(), Guid.Empty,
                ProcessingGraphDeliveryFactKind.Accepted, now),
            new ProcessingGraphDeliveryFactV1(
                ProcessingGraphDeliverySchemaVersions.Current, Guid.NewGuid(), Guid.NewGuid(),
                ProcessingGraphDeliveryFactKind.Retrieved, now),
            new ProcessingGraphDeliveryFactV1(
                ProcessingGraphDeliverySchemaVersions.Current, Guid.NewGuid(), Guid.NewGuid(),
                ProcessingGraphDeliveryFactKind.Accepted, now.AddMinutes(6))
        };
        foreach (var fact in invalidFacts)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await service.AcknowledgeAsync(registration, fact, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await service.AcknowledgeAsync(
                registration,
                new(
                    ProcessingGraphDeliverySchemaVersions.Current,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ProcessingGraphDeliveryFactKind.Rejected,
                    now,
                    ReasonCode: "not-found"),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DeliveryTreatsStepAliasesAsOrdinalIdentitiesAndIgnoresDisabledNodeAliases()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        using var telemetry = new ProcessingGraphCatalogTelemetry(clock);
        var setup = await SeedDeliveryAsync(context, now, includeDisabledNode: true).ConfigureAwait(false);
        var catalog = CreateCatalog(context, clock, telemetry);
        var service = new ProcessingGraphDeliveryService(
            context, catalog, clock, telemetry, NullLogger<ProcessingGraphDeliveryService>.Instance);
        var request = new ProcessingGraphProposalPollRequestV1(
            ProcessingGraphDeliverySchemaVersions.Current,
            setup.Registration.DeviceId,
            null,
            null,
            null,
            ProcessingGraphAgentCapabilities.Create([BuiltInProcessingRecipes.EncodedPreview.ToUpperInvariant()]));

        // The CameraAgent matches stable aliases ordinally, so a case-variant capability must not receive a proposal.
        var caseVariant = await service.PullAsync(setup.Registration, request, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphProposalPollDisposition.Incompatible, caseVariant.Disposition);
        Assert.AreEqual("step-alias-unavailable", caseVariant.ReasonCode);

        // Disabled nodes carry no executable alias on either host, so an unknown disabled alias stays deliverable.
        var exact = await service.PullAsync(
            setup.Registration,
            request with { Capabilities = ProcessingGraphAgentCapabilities.Create([BuiltInProcessingRecipes.EncodedPreview]) },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphProposalPollDisposition.Proposed, exact.Disposition);
        Assert.IsTrue(exact.Proposal!.Definition.Nodes.Any(node => !node.Enabled && node.StepAlias == "not-installed"));
    }

    private static ProcessingGraphCatalogService CreateCatalog(
        ApplicationDbContext context,
        TimeProvider clock,
        ProcessingGraphCatalogTelemetry telemetry)
    {
        var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
        return new(context, registry, clock, telemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
    }

    private static async Task<DeliverySetup> SeedDeliveryAsync(
        ApplicationDbContext context,
        DateTimeOffset now,
        bool includeDisabledNode = false)
    {
        var observatory = new Observatory
        {
            OwnerUserId = "owner",
            Name = "Delivery Observatory",
            CreatedAtUtc = now.AddDays(-1),
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            DeviceId = "agent",
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            OwnerUserId = observatory.OwnerUserId,
            IssuedAtUtc = now.AddDays(-1),
            Status = DeviceRegistrationStatus.Active
        };
        var camera = new LogicalCamera
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            Slug = "delivery-camera",
            Name = "Delivery Camera",
            Description = "Test",
            CreatedAtUtc = now.AddDays(-1),
            CreatedByUserId = observatory.OwnerUserId
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = registration.Id,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = now.AddDays(-1),
            AssignedByUserId = observatory.OwnerUserId,
            AssignmentReasonCode = "test"
        };
        var definition = CreateDefinition("delivery", "1");
        if (includeDisabledNode)
        {
            definition = definition with
            {
                Nodes =
                [
                    .. definition.Nodes,
                    new ProcessingGraphNodeDefinition(
                        "Disabled",
                        "not-installed",
                        "cameraagent-v2-disabled",
                        ProcessingOperationKind.Transform,
                        false,
                        ProcessingGraphNodeFailurePolicy.Optional,
                        10,
                        CaptureContractJson.SerializeToElement(new { }),
                        [new ProcessingGraphDependencyDefinition("$raw", ProcessingGraphDependencyKind.Ordering)],
                        [],
                        [],
                        null,
                        [],
                        [ProcessingGraphHosts.CameraAgent])
                ]
            };
        }
        var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
        var edge = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.CameraAgent, [])).Plan!;
        var central = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.PlanIdentitySha256,
            EdgePlanIdentitySha256 = edge.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.PlanIdentitySha256,
            CreatedAtUtc = now.AddMinutes(-2),
            CreatedByUserId = "editor",
            PublishedAtUtc = now.AddMinutes(-1),
            PublishedByUserId = "editor"
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Edge,
            Scope = CentralProcessingGraphAssignmentScope.GlobalDefault,
            EffectiveFromUtc = now.AddMinutes(-1),
            EffectiveUntilUtc = now.AddHours(2).AddMinutes(30),
            CreatedAtUtc = now.AddMinutes(-1),
            ActorUserId = "editor",
            ReasonCode = "test"
        };
        context.AddRange(observatory, registration, camera, installation, revision, assignment);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return new(registration, assignment, revision);
    }

    private static ProcessingGraphDefinition CreateDefinition(
        string name,
        string revision,
        string nodeId = "Preview",
        JsonElement? options = null,
        ImmutableArray<string> hostApplicability = default)
    {
        _ = BuiltInProcessingRecipes.TryGetDefinition(
            BuiltInProcessingRecipes.EncodedPreview, out var recipeDefinition);
        var normalized = options ?? BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview,
            CaptureContractJson.SerializeToElement(new EncodedPreviewOptions()));
        return new(
            ProcessingGraphSchemaVersions.Current,
            name,
            revision,
            [new ProcessingGraphSourceDefinition(
                "$raw",
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData)])],
            [new ProcessingGraphNodeDefinition(
                nodeId,
                BuiltInProcessingRecipes.EncodedPreview,
                CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
                ProcessingOperationKind.Transform,
                true,
                ProcessingGraphNodeFailurePolicy.Required,
                0,
                normalized,
                [new ProcessingGraphDependencyDefinition("$raw")],
                [new ProcessingGraphInputContract(
                    [FrameArtifactRole.Raw],
                    [ProcessingProductKind.PixelData],
                    [],
                    [],
                    [])],
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Preview,
                    CentralDerivativeRecipeCatalog.PreviewVariant,
                    ProcessingProductKind.PixelData,
                    recipeDefinition,
                    MediaType: "image/jpeg")],
                null,
                [],
                hostApplicability.IsDefault ? [] : hostApplicability)]);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record DeliverySetup(
        DeviceRegistration Registration,
        CentralProcessingGraphAssignment Assignment,
        CentralProcessingGraphRevision Revision);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
