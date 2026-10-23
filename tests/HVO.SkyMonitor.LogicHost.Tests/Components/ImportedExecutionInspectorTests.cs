using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2025:Do not pass disposables into unawaited tasks", Justification = "Held reads are completed and awaited before disposing the test context.")]
public sealed class ImportedExecutionInspectorTests
{
    private static readonly Guid Observatory = Guid.NewGuid();
    private static readonly Guid Camera = Guid.NewGuid();
    private static readonly Guid Installation = Guid.NewGuid();
    private static readonly Guid Binding = Guid.NewGuid();
    private static readonly ImportedExecutionQuery Selection = new() { Installation = Installation, Binding = Binding, Unit = 42, Node = "preview" };

    [TestMethod]
    public void DeliveredFixtureDisplaysImmutableReceiptProvenanceAndExactLineageWithoutImageOrMutation()
    {
        using var context = Context(out var read, out _);
        var cut = Render(context);
        var envelope = read.Result!.Envelope!;
        cut.Markup.Should().Contain(envelope.EvidenceId.ToString()).And.Contain(envelope.Origin.IdentitySha256)
            .And.Contain(envelope.PayloadSha256).And.Contain(envelope.Execution!.ExecutionId.ToString())
            .And.Contain("Acknowledged").And.Contain("Sequence conflicts").And.Contain("Origins with gaps")
            .And.Contain("Input ordinal 0").And.Contain("Output ordinal 0").And.Contain("window position")
            .And.Contain("Attempt 1").And.Contain("Recorded execution status");
        cut.FindAll("img").Should().BeEmpty();
        cut.FindAll("button").Select(b => b.TextContent).Should().NotContain(b => b.Contains("retry", StringComparison.OrdinalIgnoreCase));
        cut.Markup.Should().NotContain("LeaseOwner");
        read.Requests.Should().ContainSingle();
        read.Requests[0].Query.Should().Be(Selection);
    }

    [TestMethod]
    public void AllTupleMembersAreExactAndNullNeverActsAsWildcard()
    {
        var reference = Fixture("execution-live").Execution!.Nodes[0].Inputs[0].Artifact;
        var view = Reference(reference);
        ImportedExecutionInspectorReadService.Matches(view, reference).Should().BeTrue();
        foreach (var changed in new[] { view with { PayloadSha256 = null }, view with { CaptureId = Guid.NewGuid() },
            view with { ArtifactId = Guid.NewGuid() }, view with { PayloadLength = 999 }, view with { MediaType = "wrong" },
            view with { Role = "wrong" }, view with { Variant = "wrong" }, view with { DescriptorSha256 = "wrong" },
            view with { OutputIdentitySha256 = "wrong" } })
            ImportedExecutionInspectorReadService.Matches(changed, reference).Should().BeFalse();
    }

    [TestMethod]
    public void ArtifactWithSimilarRoleButDifferentTupleHasNoReconciliationLink()
    {
        using var context = Context(out var read, out _);
        var snapshot = read.Result!;
        read.Result = snapshot with
        {
            Artifacts = snapshot.Artifacts! with
            { Artifacts = snapshot.Artifacts.Artifacts.Select(a => a with { PayloadLength = 999 }).ToArray() }
        };
        var cut = Render(context);
        cut.FindAll(".imported-inputs a").Should().BeEmpty();
        cut.Markup.Should().Contain("No exact reconciliation mapping on the current artifact page");
    }

    [TestMethod]
    [DataRow("Missing")]
    [DataRow("Unresolved")]
    [DataRow("Quarantined")]
    [DataRow("Available")]
    public void CentralObservationIsIndependentFromReceiptAndProducerFacts(string state)
    {
        using var context = Context(out var read, out _);
        var originalHash = read.Result!.Envelope!.PayloadSha256;
        var original = read.Result;
        var centralCapture = Guid.NewGuid();
        read.Result = original with { Reconciliation = Reconciliation(original, state), ViewerCapture = state == "Available" ? centralCapture : null };
        var cut = Render(context);
        cut.FindAll(".imported-observations li").Should().ContainSingle();
        cut.Markup.Should().Contain(state).And.Contain("central-test-reason").And.Contain("generation-test")
            .And.Contain(originalHash).And.Contain("Acknowledged");
        cut.FindAll("a").Count(a => a.TextContent == "Open exact capture viewer").Should().Be(state == "Available" ? 1 : 0);
        if (state == "Available") cut.FindAll("a").Single(a => a.TextContent == "Open exact capture viewer").GetAttribute("href")
            .Should().EndWith($"/archive/{centralCapture:D}");
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public void EmptyRepeatedAndTruncatedObservationsStayExplicit()
    {
        using var context = Context(out var read, out _);
        var snapshot = read.Result!;
        var reconciliation = Reconciliation(snapshot, "Missing");
        read.Result = snapshot with { Reconciliation = reconciliation with { Observations = [] } };
        var cut = Render(context);
        cut.Markup.Should().Contain("No retained central observation").And.Contain("availability is unknown");
        read.Result = snapshot with
        {
            Reconciliation = reconciliation with
            {
                Observations = Enumerable.Range(1, 50).Reverse().Select(id => reconciliation.Observations[0] with { Id = id }).ToArray(),
                ObservationsTruncated = true
            }
        };
        cut.Render();
        cut.FindAll(".imported-observations li").Should().HaveCount(50);
        cut.Markup.Should().Contain("latest 50 only, including repeated states");
    }

    [TestMethod]
    public void ContinuationsRemainIndependentExplicitLinksAndNeverDrain()
    {
        using var context = Context(out var read, out _);
        var snapshot = read.Result!;
        read.Result = snapshot with
        {
            Units = new(snapshot.Units!.Items, "units-opaque"),
            Origins = new([], "origins-opaque"),
            Artifacts = snapshot.Artifacts! with { NextCursor = "artifact-opaque" },
            Nodes = new(snapshot.Nodes!.Items, "nodes-opaque"),
            AttemptsNext = "attempts-opaque",
            InputsNext = "inputs-opaque",
            OutputsNext = "outputs-opaque"
        };
        var cut = Render(context);
        foreach (var (label, parameter) in new[] { ("Next evidence page", "unitsPage=units-opaque"),
            ("Next origin page", "originsPage=origins-opaque"), ("Next artifact page", "artifactsPage=artifact-opaque"),
            ("Next node page", "nodesPage=nodes-opaque"), ("Next attempt page", "attemptsPage=attempts-opaque"),
            ("Next input page", "inputsPage=inputs-opaque"), ("Next output page", "outputsPage=outputs-opaque") })
            cut.FindAll("a").Single(a => a.TextContent == label).GetAttribute("href").Should().Contain(parameter);
        read.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public void ExplicitCentralComparisonPreservesImportedUnitAndUsesOnlyEnteredUuid()
    {
        using var context = Context(out var read, out _);
        var cut = Render(context);
        var id = Guid.NewGuid();
        cut.Find("#central-execution-identity").Change(id.ToString("D"));
        cut.Find("form").Submit();
        var uri = context.Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().Contain($"central={id:D}").And.Contain("unit=42").And.Contain($"binding={Binding:D}");
        read.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public void MissingCentralExecutionAndNoNodesNeverImplySuccess()
    {
        using var context = Context(out var read, out _);
        read.Result = read.Result! with { CentralRequested = true, Central = null, Nodes = new([], null), Node = null };
        var cut = Render(context);
        cut.Markup.Should().Contain("Central execution unavailable or not authorized").And.Contain("No recorded nodes")
            .And.Contain("Success is not inferred");
    }

    [TestMethod]
    public void MissingInstallationBindingAndUnitEvidenceHaveExplicitUnavailableStates()
    {
        using var context = Context(out var read, out _);
        read.Result = new(new([], null));
        var cut = Render(context);
        cut.Markup.Should().Contain("Imported evidence unavailable: no retained installations");
        read.Result = new(new([], null), new([], false));
        cut.Render();
        cut.Markup.Should().Contain("no imported binding for this installation");
        read.Result = Snapshot() with { Units = new([], null), Envelope = null, Artifacts = null, Node = null, Nodes = null };
        cut.Render();
        cut.Markup.Should().Contain("Imported evidence unavailable on this page");
    }

    [TestMethod]
    public void RevisionAndAvailabilityFixturesKeepOriginClaimsSeparateFromProduction()
    {
        using var context = Context(out var read, out _);
        var snapshot = read.Result!;
        read.Result = snapshot with { Envelope = Fixture("revision-local"), Node = null };
        var cut = Render(context);
        cut.Markup.Should().Contain("Imported graph revision").And.Contain("authenticated producer claims").And.Contain("No central assignment provenance");
        var envelope = Fixture("availability");
        read.Result = snapshot with
        {
            Envelope = envelope with { Availability = envelope.Availability! with { Observations = [] } },
            Availability = new(envelope.Availability!.Observations, "next-report-page"),
            Node = null
        };
        cut.Render();
        cut.Markup.Should().Contain("Report membership does not establish production").And.Contain("Producer observed");
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public async Task RefreshClearsMetadataAndChildrenBeforeAuthorityCompletes()
    {
        using var context = Context(out var read, out _);
        var cut = Render(context);
        var pending = new TaskCompletionSource<ImportedExecutionSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        read.Load = _ => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.WaitForAssertion(() => cut.FindAll(".imported-exact").Should().BeEmpty());
        cut.FindAll(".artifact-identity").Should().BeEmpty();
        cut.Find(".imported-inspector").GetAttribute("aria-busy").Should().Be("true");
        pending.SetResult(null);
        await refresh;
        cut.Markup.Should().Contain("access was removed").And.NotContain("Payload SHA-256");
    }

    [TestMethod]
    public async Task AuthenticationLossRejectsAnOldActorResponseEvenWhenItIgnoresCancellation()
    {
        using var context = Context(out var read, out var auth);
        var cut = Render(context);
        var old = read.Result;
        var pending = new TaskCompletionSource<ImportedExecutionSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        read.Load = _ => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        await cut.InvokeAsync(() => auth.Change(new ClaimsPrincipal()));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("access was removed"));
        pending.SetResult(old);
        await refresh;
        cut.FindAll(".imported-exact,.artifact-identity").Should().BeEmpty();
    }

    [TestMethod]
    public async Task ScopeChangeCancelsAndRejectsOldResponse()
    {
        using var context = Context(out var read, out _);
        var pending = new TaskCompletionSource<ImportedExecutionSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = read.Result;
        read.Load = _ => pending.Task;
        var cut = Render(context);
        read.Load = _ => Task.FromResult<ImportedExecutionSnapshot?>(null);
        cut.Render(p => p.Add(c => c.CameraId, Guid.NewGuid()));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("access was removed"));
        pending.SetResult(old);
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.FindAll(".imported-exact").Should().BeEmpty();
        read.Tokens.First().IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public async Task FailureAndReconnectClearEvidenceAndRecoverOnlyAfterFreshRead()
    {
        using var context = Context(out var read, out _);
        var cut = Render(context);
        read.Load = _ => throw new InvalidOperationException("private database diagnostic");
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.Markup.Should().Contain("temporarily unavailable").And.NotContain("private database");
        cut.FindAll(".imported-exact").Should().BeEmpty();
        read.Load = _ => Task.FromResult(read.Result);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.FindAll(".imported-exact").Should().ContainSingle();
    }

    [TestMethod]
    public void ComparisonShowsIndependentCentralStatusSourcesAndPlanIdentity()
    {
        using var context = Context(out var read, out _);
        var id = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var snapshot = read.Result!;
        read.Result = snapshot with
        {
            CentralRequested = true,
            Central = new(
            new(id, "Replay", "Failed", revision, Observatory, Camera, Guid.NewGuid(), Guid.NewGuid(), "replay", "actor", "central-reason",
                1, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null), Installation, null, new string('A', 64), new string('B', 64),
            new string('C', 64), 1, 0, 0, 0, null, null, null, null, null,
            [new(0, "primary", Guid.NewGuid(), "source-checksum", 12, DateTimeOffset.UnixEpoch)], new([], null))
        };
        var cut = Render(context);
        cut.Markup.Should().Contain(id.ToString()).And.Contain(revision.ToString()).And.Contain("Failed / central-reason")
            .And.Contain("source-checksum").And.Contain("Different").And.Contain("distinct host plan");
        cut.Markup.Should().Contain(snapshot.Envelope!.Execution!.ExecutionId.ToString());
        cut.FindAll("button").Should().HaveCount(2);
    }

    [TestMethod]
    public async Task DisconnectionSuspensionClearsMetadataAndInvalidatesPendingResponses()
    {
        using var context = Context(out var read, out _);
        var cut = Render(context);
        var old = read.Result;
        var pending = new TaskCompletionSource<ImportedExecutionSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        read.Load = _ => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        await cut.InvokeAsync(cut.Instance.SuspendAsync);
        cut.Markup.Should().Contain("Session unavailable");
        pending.SetResult(old);
        await refresh;
        cut.FindAll(".imported-exact,.artifact-identity").Should().BeEmpty();
        read.Tokens.Last().IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public async Task CredentialScopeLossAndAnonymousCallNeverInvokeProtectedRead()
    {
        using var context = Context(out var read, out var auth);
        auth.Change(new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "user"), new("account_type", "User"),
            new(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new(ApiKeyClaims.AccessLevel, "Read"), new(ApiKeyClaims.ObservatoryId, Guid.NewGuid().ToString("D"))],
            ApiKeyAuthenticationOptions.AuthenticationScheme)));
        var cut = Render(context);
        cut.Markup.Should().Contain("access was removed");
        read.Requests.Should().BeEmpty();
        await cut.InvokeAsync(() => auth.Change(new ClaimsPrincipal()));
        read.Requests.Should().BeEmpty();
    }

    private static IRenderedComponent<ImportedExecutionInspector> Render(BunitContext context) => context.Render<ImportedExecutionInspector>(p =>
        p.Add(c => c.ObservatoryId, Observatory).Add(c => c.CameraId, Camera).Add(c => c.Query, Selection));

    private static BunitContext Context(out Read read, out Authentication auth)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./Components/Shared/ImportedExecutionInspector.razor.js");
        module.Setup<bool>("hasSession", _ => true).SetResult(true);
        read = new Read(); auth = new Authentication();
        context.Services.AddSingleton<IImportedExecutionInspectorReadService>(read);
        context.Services.AddSingleton<AuthenticationStateProvider>(auth);
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddLogging();
        context.Renderer.SetRendererInfo(new RendererInfo("Server", true));
        return context;
    }

    internal static ExecutionEvidenceEnvelopeV1 Fixture(string name) => GraphExecutionEvidenceJson.ParseEnvelope(
        File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", $"cameraagent-execution-evidence-{name}-v1.json"))).Value!;

    private static ExecutionEvidenceArtifactView Reference(ExecutionEvidenceArtifactReferenceV1 value) => new(7, 0, value.ArtifactId,
        value.CaptureId, value.OutputIdentitySha256, value.PayloadSha256, value.DescriptorSha256, value.PayloadLength,
        value.MediaType, value.Role?.ToString(), value.Variant);

    private static ImportedExecutionSnapshot Snapshot()
    {
        var envelope = Fixture("execution-live");
        var execution = envelope.Execution!;
        var unit = new ExecutionEvidenceUnitView(42, 1, envelope.OriginSequence, envelope.EvidenceId, envelope.PayloadSha256,
            envelope.Kind.ToString(), execution.GraphRevisionId, execution.ExecutionId, execution.CaptureId,
            envelope.ProducedAtUtc, envelope.ProducedAtUtc.AddHours(1), true, null);
        var binding = new ExecutionEvidenceBindingView(Binding, Observatory, Guid.NewGuid(), Installation, Guid.NewGuid(),
            envelope.Origin.OriginInstallationId, envelope.Origin.AgentInstanceId, DateTimeOffset.UnixEpoch);
        var association = new ExecutionEvidenceUnitAssociationView(Binding, binding.InstallationPublicId, binding.OriginInstallationId,
            binding.AgentInstanceId, envelope.Origin.IdentitySha256, unit);
        var node = execution.Nodes[0];
        return new(new([new(Installation, binding.InstallationPublicId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1))], null),
            new([binding], false), binding, new(Binding, 1024, 3, 1, 1, 0, false, 100000, 1000, 16, 16, 100, 1, DateTimeOffset.UnixEpoch, 1, false, DateTimeOffset.UnixEpoch),
            new([], null), new([unit], null), envelope with { Execution = execution with { Nodes = [] } },
            new(ExecutionEvidenceUnitArtifactsView.CurrentSchemaVersion, association, [Reference(node.Inputs[0].Artifact)], null),
            Receipt: new(ExecutionEvidenceFeedbackV1.CurrentSchemaVersion, envelope.Origin.IdentitySha256, DateTimeOffset.UnixEpoch,
                1, [new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, 2, 3)],
                GraphExecutionEvidenceJson.CreateReceiverFacts(envelope, null, unit.ReceivedAtUtc),
                new(ExecutionEvidenceRetentionV1.CurrentSchemaVersion, 1, DateTimeOffset.MaxValue, 1000), true),
            Nodes: new(execution.Nodes.Select(n => new ImportedNodeSummary(n.NodeId, n.Required, n.PlanSha256, n.Status, n.ReasonCode)).ToArray(), null), Node: node);
    }

    private static ExecutionEvidenceArtifactReconciliationView Reconciliation(ImportedExecutionSnapshot snapshot, string state) =>
        new(ExecutionEvidenceArtifactReconciliationView.CurrentSchemaVersion, snapshot.Artifacts!.Association, snapshot.Artifacts.Artifacts[0],
            [new(1, 7, state, "central-test-reason", DateTimeOffset.UnixEpoch, "generation-test")], false);

    private sealed class Read : IImportedExecutionInspectorReadService
    {
        internal ImportedExecutionSnapshot? Result { get; set; } = Snapshot();
        internal Func<ImportedExecutionQuery, Task<ImportedExecutionSnapshot?>>? Load { get; set; }
        internal List<(Guid Observatory, Guid Camera, ImportedExecutionQuery Query)> Requests { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        public Task<ImportedExecutionSnapshot?> ReadAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
            ImportedExecutionQuery query, CancellationToken cancellationToken = default)
        { Requests.Add((observatoryId, cameraId, query)); Tokens.Add(cancellationToken); return Load?.Invoke(query) ?? Task.FromResult(Result); }
    }

    private sealed class Authentication : AuthenticationStateProvider
    {
        private Task<AuthenticationState> current = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user"), new Claim("account_type", "User")], "Identity.Application"))));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => current;
        internal void Change(ClaimsPrincipal principal) { current = Task.FromResult(new AuthenticationState(principal)); NotifyAuthenticationStateChanged(current); }
    }
}
