using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    [TestMethod]
    [DataRow((int)ObservatoryMembershipRole.Viewer)]
    [DataRow((int)ObservatoryMembershipRole.Manager)]
    [DataRow((int)ObservatoryMembershipRole.Owner)]
    public async Task ImportedInspectorUsesCurrentMembershipAndExactRetainedRoute(int role)
    {
        var a = await SeedInspectorAsync(30).ConfigureAwait(false);
        var b = await SeedInspectorAsync(3).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.ObservatoryMemberships.Where(m => m.ObservatoryId == a.Observatory && m.UserId == a.User)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, (ObservatoryMembershipRole)role)).ConfigureAwait(false);
        var service = scope.ServiceProvider.GetRequiredService<IImportedExecutionInspectorReadService>();
        var principal = LogicalCameraArchiveIntegrationTests.Principal(a.User);
        var selection = a.Selection with { Unit = a.ExecutionUnit, Node = "preview" };
        var read = await service.ReadAsync(principal, a.Observatory, a.Camera, selection).ConfigureAwait(false);
        Assert.IsNotNull(read);
        Assert.AreEqual(a.ExecutionHash, read.Envelope!.PayloadSha256);
        Assert.IsNotNull(read.Node);
        Assert.IsNotEmpty(read.Node.Inputs);
        Assert.IsNotEmpty(read.Node.Attempts);
        Assert.IsNotEmpty(read.Node.Outputs);
        Assert.IsNull(await service.ReadAsync(principal, a.Observatory, b.Camera, selection).ConfigureAwait(false));
        foreach (var foreign in new[] { selection with { Installation = b.Installation }, selection with { Binding = b.Binding },
            selection with { Unit = b.ExecutionUnit } })
            Assert.IsNull(await service.ReadAsync(principal, a.Observatory, a.Camera, foreign).ConfigureAwait(false));
        var scoped = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, a.User), new("account_type", "User"),
            new(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new(ApiKeyClaims.AccessLevel, "Read"), new(ApiKeyClaims.ObservatoryId, a.Observatory.ToString("D"))],
            ApiKeyAuthenticationOptions.AuthenticationScheme));
        Assert.IsNull(await service.ReadAsync(scoped, b.Observatory, b.Camera, b.Selection).ConfigureAwait(false));
        Assert.IsNull(await service.ReadAsync(new ClaimsPrincipal(), a.Observatory, a.Camera, selection).ConfigureAwait(false));
        // Historical identity stays readable after retirement and registration revocation.
        await db.LogicalCameraInstallations.Where(i => i.Id == a.Installation)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RetiredAtUtc, Epoch)).ConfigureAwait(false);
        Assert.IsNotNull(await service.ReadAsync(principal, a.Observatory, a.Camera, selection).ConfigureAwait(false));
        await db.ObservatoryMemberships.Where(m => m.ObservatoryId == a.Observatory && m.UserId == a.User).ExecuteDeleteAsync().ConfigureAwait(false);
        Assert.IsNull(await service.ReadAsync(principal, a.Observatory, a.Camera, selection).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ImportedInspectorPagesBindScopeFilterUserAndCredentialAndDoNotReadAllHistory()
    {
        var data = await SeedInspectorAsync(70).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IImportedExecutionInspectorReadService>();
        var principal = LogicalCameraArchiveIntegrationTests.Principal(data.User);
        var first = await service.ReadAsync(principal, data.Observatory, data.Camera, data.Selection).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.HasCount(25, first.Units!.Items);
        Assert.IsNotNull(first.Units.NextCursor);
        var continuation = data.Selection with { UnitsPage = first.Units.NextCursor };
        var second = await service.ReadAsync(principal, data.Observatory, data.Camera, continuation).ConfigureAwait(false);
        Assert.IsNotNull(second);
        Assert.HasCount(25, second.Units!.Items);
        Assert.IsFalse(first.Units.Items.Select(u => u.Id).Intersect(second.Units.Items.Select(u => u.Id)).Any());
        foreach (var changed in new[] { continuation with { Kind = "GraphExecution" }, continuation with { Origin = data.OriginHash },
            continuation with { UnitsPage = first.Units.NextCursor + "tampered" } })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ReadAsync(principal, data.Observatory, data.Camera, changed)).ConfigureAwait(false);
        var scoped = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, data.User), new("account_type", "User"),
            new(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new(ApiKeyClaims.AccessLevel, "Read"), new(ApiKeyClaims.ObservatoryId, data.Observatory.ToString("D"))], ApiKeyAuthenticationOptions.AuthenticationScheme));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ReadAsync(scoped, data.Observatory, data.Camera, continuation)).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadAsync(principal, data.Observatory, data.Camera, data.Selection, cancellation.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ImportedInspectorCanonicalChildrenContinueIndependentlyWithinOneImmutableUnit()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        using var producer = AssemblyHooks.Fixture.Factory.CreateClient();
        using var revision = await PostAsync(producer, device, Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device)).ConfigureAwait(false);
        _ = await FeedbackAsync(revision).ConfigureAwait(false);
        var original = Reorigin(await FixtureAsync("execution-live").ConfigureAwait(false), device);
        var node = original.Execution!.Nodes[0];
        var large = node with
        {
            Inputs = [.. Enumerable.Range(0, 32).Select(i => node.Inputs[0] with { Ordinal = i, WindowPosition = i })],
            Outputs = [.. Enumerable.Range(0, 32).Select(i => node.Outputs[0] with { Ordinal = i })],
            Attempts = [.. Enumerable.Range(1, 32).Select(i => node.Attempts[0] with { AttemptNumber = i })]
        };
        var envelope = GraphExecutionEvidenceJson.Seal(original with
        {
            OriginSequence = 2,
            Execution = original.Execution with { Nodes = original.Execution.Nodes.SetItem(0, large) }
        });
        using var submitted = await PostAsync(producer, device, envelope).ConfigureAwait(false);
        Assert.AreEqual(4, (await FeedbackAsync(submitted).ConfigureAwait(false)).Facts.Length);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var evidence = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>();
        var units = await evidence.ListUnitsAsync(device.BindingId, device.OwnerId, null, kind: "GraphExecution").ConfigureAwait(false);
        var camera = await BindingCameraAsync(device).ConfigureAwait(false);
        var service = scope.ServiceProvider.GetRequiredService<IImportedExecutionInspectorReadService>();
        var principal = LogicalCameraArchiveIntegrationTests.Principal(device.OwnerId);
        var query = new ImportedExecutionQuery { Installation = device.InstallationId, Binding = device.BindingId, Unit = units!.Single().Id, Node = large.NodeId };
        var first = (await service.ReadAsync(principal, device.ObservatoryId, camera, query).ConfigureAwait(false))!;
        Assert.HasCount(25, first.Node!.Inputs);
        Assert.HasCount(25, first.Node.Attempts);
        Assert.HasCount(25, first.Node.Outputs);
        Assert.IsNotNull(first.InputsNext);
        Assert.IsNotNull(first.AttemptsNext);
        Assert.IsNotNull(first.OutputsNext);
        var next = (await service.ReadAsync(principal, device.ObservatoryId, camera, query with { InputsPage = first.InputsNext }).ConfigureAwait(false))!;
        CollectionAssert.AreEqual(Enumerable.Range(25, 7).ToArray(), next.Node!.Inputs.Select(i => i.Ordinal).ToArray());
        Assert.HasCount(25, next.Node.Attempts);
        Assert.HasCount(25, next.Node.Outputs);
        Assert.IsNull(next.InputsNext);
        Assert.AreEqual(envelope.PayloadSha256, next.Envelope!.PayloadSha256);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ReadAsync(principal, device.ObservatoryId, camera,
            query with { InputsPage = first.AttemptsNext })).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ReadAsync(principal, device.ObservatoryId, camera,
            query with { Node = "annotate", InputsPage = first.InputsNext })).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ImportedInspectorFinalRecheckConcealsRevocationDuringCanonicalRead()
    {
        var data = await SeedInspectorAsync(3).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var removed = false;
        using var probe = new ImportedInspectorProbe
        {
            Executed = command =>
            {
                if (removed || !command.Text.Contains("[CanonicalBytes]", StringComparison.Ordinal)) return;
                removed = true;
                using var revoke = AssemblyHooks.Fixture.Factory.Services.CreateScope();
                revoke.ServiceProvider.GetRequiredService<ApplicationDbContext>().ObservatoryMemberships
                    .Where(m => m.ObservatoryId == data.Observatory && m.UserId == data.User).ExecuteDelete();
            }
        };
        var result = await probe.RunAsync(() => services.GetRequiredService<IImportedExecutionInspectorReadService>()
            .ReadAsync(LogicalCameraArchiveIntegrationTests.Principal(data.User), data.Observatory, data.Camera,
                data.Selection with { Unit = data.ExecutionUnit })).ConfigureAwait(false);
        Assert.IsTrue(removed);
        Assert.IsNull(result);
    }

    // The three selected fixtures pass through the real #538 receiver. Larger histories are valid sealed
    // metadata-only availability records seeded directly; this measures reads, not ingestion throughput.
    internal static async Task<ImportedInspectorDataset> SeedInspectorAsync(int history)
    {
        var device = await SeedAsync().ConfigureAwait(false);
        using var producer = AssemblyHooks.Fixture.Factory.CreateClient();
        var names = new[] { "revision-local", "execution-live", "availability" };
        ExecutionEvidenceEnvelopeV1? execution = null;
        ExecutionEvidenceEnvelopeV1? report = null;
        for (var index = 0; index < names.Length; index++)
        {
            var envelope = GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync(names[index]).ConfigureAwait(false), device) with { OriginSequence = index + 1 });
            using var response = await PostAsync(producer, device, envelope).ConfigureAwait(false);
            Assert.AreEqual(4, (await FeedbackAsync(response).ConfigureAwait(false)).Facts.Length);
            if (index == 1) execution = envelope;
            if (index == 2) report = envelope;
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var origin = await db.Set<ImportedExecutionEvidenceOrigin>().SingleAsync(o => o.BindingId == device.BindingId).ConfigureAwait(false);
        var executionUnit = await db.Set<ImportedExecutionEvidenceUnit>().Where(u => u.BindingId == device.BindingId && u.Kind == "GraphExecution")
            .Select(u => u.Id).SingleAsync().ConfigureAwait(false);
        long extraBytes = 0;
        var pendingUnits = new List<ImportedExecutionEvidenceUnit>();
        async Task FlushAsync()
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
            var references = new List<ImportedExecutionEvidenceArtifact>();
            foreach (var unit in pendingUnits)
            {
                var reference = report!.Availability!.Observations[0].Artifact;
                var artifact = new ImportedExecutionEvidenceArtifact
                {
                    BindingId = device.BindingId,
                    UnitId = unit.Id,
                    Ordinal = 0,
                    ArtifactId = reference.ArtifactId,
                    CaptureId = reference.CaptureId,
                    OutputIdentitySha256 = reference.OutputIdentitySha256,
                    PayloadSha256 = reference.PayloadSha256,
                    DescriptorSha256 = reference.DescriptorSha256,
                    PayloadLength = reference.PayloadLength,
                    MediaType = reference.MediaType,
                    Role = reference.Role?.ToString(),
                    Variant = reference.Variant
                };
                db.Add(artifact); references.Add(artifact);
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
            foreach (var reference in references) db.Add(new ExecutionEvidenceArtifactCheck
            { BindingId = device.BindingId, ArtifactReferenceId = reference.Id, NextCheckAtUtc = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync().ConfigureAwait(false);
            pendingUnits.Clear();
            db.ChangeTracker.Clear();
        }
        for (var sequence = 4; sequence <= history; sequence++)
        {
            var envelope = GraphExecutionEvidenceJson.Seal(report! with
            {
                EvidenceId = Guid.NewGuid(),
                OriginSequence = sequence,
                Availability = report!.Availability! with { Observations = [report.Availability.Observations[0]] }
            });
            var bytes = GraphExecutionEvidenceJson.Serialize(envelope);
            Assert.IsTrue(GraphExecutionEvidenceJson.ParseEnvelope(bytes).Validation.IsValid);
            var stored = new ImportedExecutionEvidenceUnit
            {
                BindingId = device.BindingId,
                OriginId = origin.Id,
                OriginSequence = sequence,
                EvidenceId = envelope.EvidenceId,
                PayloadSha256 = envelope.PayloadSha256,
                Kind = envelope.Kind.ToString(),
                ExecutionId = envelope.Availability!.ExecutionId,
                ProducedAtUtc = envelope.ProducedAtUtc,
                ReceivedAtUtc = Epoch.AddSeconds(sequence),
                CanonicalBytes = bytes,
                Accepted = true
            };
            db.Add(stored); pendingUnits.Add(stored);
            extraBytes += bytes.Length;
            if (sequence % 256 == 0) await FlushAsync().ConfigureAwait(false);
        }
        await FlushAsync().ConfigureAwait(false);
        // Mutable receipt progress/usage counters are test workload bookkeeping, never evidence corrections.
        await db.Set<ExecutionEvidenceBindingUsage>().Where(u => u.BindingId == device.BindingId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.UnitCount, history).SetProperty(u => u.StoredBytes, u => u.StoredBytes + extraBytes)).ConfigureAwait(false);
        await db.Set<ImportedExecutionEvidenceOrigin>().Where(o => o.Id == origin.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.HighestSeenSequence, (long)history).SetProperty(o => o.ContiguousThroughSequence, (long)history)).ConfigureAwait(false);
        await db.Set<ImportedExecutionEvidenceRange>().Where(r => r.OriginId == origin.Id && r.FromSequence == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ToSequence, (long)history)).ConfigureAwait(false);
        var camera = await db.LogicalCameraInstallations.Where(i => i.Id == device.InstallationId).Select(i => i.LogicalCameraId).SingleAsync().ConfigureAwait(false);
        return new(device.OwnerId, device.ObservatoryId, camera, device.InstallationId, device.BindingId, executionUnit,
            execution!.PayloadSha256, device.Origin.IdentitySha256);
    }
}

internal sealed record ImportedInspectorDataset(string User, Guid Observatory, Guid Camera, Guid Installation, Guid Binding,
    long ExecutionUnit, string ExecutionHash, string OriginHash)
{
    internal ImportedExecutionQuery Selection => new() { Installation = Installation, Binding = Binding };
    internal string Path => $"/app/network/observatories/{Observatory:D}/cameras/{Camera:D}/imported-executions";
    internal string Url => $"{Path}?installation={Installation:D}&binding={Binding:D}";
}

// Exact Activity trace captures both the app DbContext and the importer's isolated SQL pool, without
// replacing either production context. Replays use existing CameraOperationsSql physical-plan tooling.
internal sealed class ImportedInspectorProbe : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly IDisposable all;
    private readonly List<IDisposable> listeners = [];
    private ActivityTraceId trace;
    internal ConcurrentQueue<CameraOperationsSql.Command> Commands { get; } = new();
    internal Action<CameraOperationsSql.Command>? Executed { get; init; }
    internal ImportedInspectorProbe() => all = DiagnosticListener.AllListeners.Subscribe(this);
    internal async Task<T> RunAsync<T>(Func<Task<T>> read)
    {
        using var activity = new Activity("issue1240-read").SetIdFormat(ActivityIdFormat.W3C).Start();
        trace = activity.TraceId;
        try { return await read().ConfigureAwait(false); }
        finally { trace = default; }
    }
    public void OnNext(DiagnosticListener value) { if (value.Name == "Microsoft.EntityFrameworkCore") listeners.Add(value.Subscribe(this)); }
    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted" || value.Value is not CommandExecutedEventData data ||
            Activity.Current?.TraceId != trace) return;
        var command = new CameraOperationsSql.Command(data.Command.CommandText, data.Command.Parameters.Cast<SqlParameter>().Select(CameraOperationsSql.Copy).ToArray());
        Commands.Enqueue(command);
        Executed?.Invoke(command);
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose() { all.Dispose(); foreach (var listener in listeners) listener.Dispose(); }
}
