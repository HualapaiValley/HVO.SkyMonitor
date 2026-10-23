using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record ImportedInstallation(Guid Id, Guid PublicId, DateTimeOffset AssignedUtc, DateTimeOffset? RetiredUtc);
internal sealed record ImportedNodeSummary(string Id, bool Required, string PlanSha256, ExecutionEvidenceNodeStatus Status, string? Reason);
internal sealed record ImportedExecutionSnapshot(
    CameraOperationsPage<ImportedInstallation> Installations,
    ExecutionEvidenceInstallationBindingsView? Bindings = null,
    ExecutionEvidenceBindingView? Binding = null,
    ExecutionEvidenceStatusView? Status = null,
    CameraOperationsPage<ExecutionEvidenceOriginView>? Origins = null,
    CameraOperationsPage<ExecutionEvidenceUnitView>? Units = null,
    ExecutionEvidenceEnvelopeV1? Envelope = null,
    ExecutionEvidenceUnitArtifactsView? Artifacts = null,
    ExecutionEvidenceArtifactReconciliationView? Reconciliation = null,
    ExecutionEvidenceFeedbackV1? Receipt = null,
    CameraOperationsPage<ImportedNodeSummary>? Nodes = null,
    ExecutionEvidenceNodeV1? Node = null,
    string? AttemptsNext = null, string? InputsNext = null, string? OutputsNext = null,
    CameraOperationsPage<ArtifactAvailabilityObservationV1>? Availability = null,
    CameraProcessingGraphExecutionInspector? Central = null,
    bool CentralRequested = false,
    Guid? ViewerCapture = null);

internal interface IImportedExecutionInspectorReadService
{
    Task<ImportedExecutionSnapshot?> ReadAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        ImportedExecutionQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Ephemeral view composition over delivered reads. No import, receipt or reconciliation writes.</summary>
internal sealed class ImportedExecutionInspectorReadService(ApplicationDbContext db, ExecutionEvidenceService evidence,
    ICameraOperationsReadService central, IDataProtectionProvider protection) : IImportedExecutionInspectorReadService
{
    internal const int PageSize = 25;
    private readonly IDataProtector protector = protection.CreateProtector("imported-execution-ui-pages-v1");

    public async Task<ImportedExecutionSnapshot?> ReadAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        ImportedExecutionQuery query, CancellationToken cancellationToken = default)
    {
        var user = CentralArtifactCredentialAccess.GetOwnerId(principal);
        var credential = CentralArtifactCredentialAccess.GetObservatoryScope(principal);
        if (user is null || !CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
            !CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatoryId) ||
            !await CanReadAsync(user, observatoryId, cameraId, cancellationToken).ConfigureAwait(false)) return null;

        PageCursor Selection(string collection) => new(user, credential, observatoryId, cameraId, collection,
            collection == "installations" ? null : query.Installation,
            collection == "installations" ? null : query.Binding,
            collection is "installations" or "origins" or "units" ? null : query.Unit,
            collection is "attempts" or "inputs" or "outputs" ? query.Node : null,
            collection == "units" ? query.Kind : null, collection == "units" ? query.Origin : null);
        PageCursor Position(string collection, string? encoded) => Decode(Selection(collection), encoded);
        string? Next(string collection, long position, string? name, bool more) => more
            ? protector.Protect(JsonSerializer.Serialize(Selection(collection) with { Position = position, Name = name })) : null;
        CameraOperationsPage<T> Page<T>(IReadOnlyList<T> rows, string collection, Func<T, long> id)
        {
            var items = rows.Take(PageSize).ToArray();
            return new(items, Next(collection, items.Length == 0 ? 0 : id(items[^1]), null, rows.Count > PageSize));
        }

        // The existing network summary exposes only installation-public IDs. Project the retained primary key
        // here, with the same current membership predicate, so discovery never guesses between the two IDs.
        var after = Position("installations", query.InstallationsPage);
        var installs = db.LogicalCameraInstallations.AsNoTracking().Where(i => i.LogicalCameraId == cameraId);
        if (after.Name is not null)
        {
            if (!Guid.TryParse(after.Name, out var id)) throw new ArgumentException("Invalid installation page.");
            var time = new DateTimeOffset(after.Position, TimeSpan.Zero);
            installs = installs.Where(i => i.AssignedAtUtc < time || i.AssignedAtUtc == time && i.InstallationPublicId.CompareTo(id) < 0);
        }
        var installationRows = await installs.OrderByDescending(i => i.AssignedAtUtc).ThenByDescending(i => i.InstallationPublicId).Take(PageSize + 1)
            .Select(i => new ImportedInstallation(i.Id, i.InstallationPublicId, i.AssignedAtUtc, i.RetiredAtUtc))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var installationItems = installationRows.Take(PageSize).ToArray();
        var result = new ImportedExecutionSnapshot(new(installationItems,
            Next("installations", installationItems.LastOrDefault()?.AssignedUtc.Ticks ?? 0, installationItems.LastOrDefault()?.PublicId.ToString("D"), installationRows.Length > PageSize)));
        if (query.Installation is { } installation)
        {
            var bindings = await evidence.ListInstallationBindingsAsync(observatoryId, cameraId, installation, user, credential, cancellationToken).ConfigureAwait(false);
            if (bindings is null) return null;
            result = result with { Bindings = bindings };
            if (query.Binding is { } bindingId)
            {
                // A selected binding can be outside a truncated discovery page; validate it directly too.
                var binding = await evidence.GetBindingAsync(bindingId, user, credential, cancellationToken).ConfigureAwait(false);
                if (binding is null || binding.ObservatoryId != observatoryId || binding.LogicalCameraInstallationId != installation) return null;
                var origins = await evidence.ListOriginsAsync(bindingId, user, credential, Math.Max(0, Position("origins", query.OriginsPage).Position),
                    PageSize + 1, cancellationToken).ConfigureAwait(false);
                var units = await evidence.ListUnitsAsync(bindingId, user, credential, Math.Max(0, Position("units", query.UnitsPage).Position),
                    PageSize + 1, query.Origin, query.Kind, cancellationToken).ConfigureAwait(false);
                var status = await evidence.GetStatusAsync(bindingId, user, credential, cancellationToken).ConfigureAwait(false);
                if (origins is null || units is null || status is null) return null;
                result = result with { Binding = binding, Status = status, Origins = Page(origins, "origins", o => o.Id), Units = Page(units, "units", u => u.Id) };
                if (query.Unit is { } unitId)
                {
                    var artifacts = await evidence.ListUnitArtifactsAsync(observatoryId, cameraId, installation, bindingId, unitId,
                        user, credential, query.ArtifactsPage, PageSize, cancellationToken).ConfigureAwait(false);
                    var bytes = artifacts is null ? null : await evidence.GetUnitAsync(bindingId, unitId, user, credential, cancellationToken).ConfigureAwait(false);
                    var envelope = bytes is null ? null : GraphExecutionEvidenceJson.ParseEnvelope(bytes).Value;
                    if (artifacts is null || envelope is null || !Matches(artifacts.Association, envelope)) return null;
                    var receipt = await evidence.GetFeedbackAsync(bindingId, envelope.Origin.IdentitySha256, user, credential,
                        envelope.OriginSequence - 1, 1, cancellationToken).ConfigureAwait(false);
                    result = result with { Envelope = envelope, Artifacts = artifacts, Receipt = receipt };
                    if (query.Artifact is { } reference)
                    {
                        var exact = artifacts.Artifacts.SingleOrDefault(a => a.ArtifactReferenceId == reference);
                        if (exact is null) throw new ArgumentException("Choose a reference on this artifact page.");
                        var observations = await evidence.GetArtifactReconciliationAsync(observatoryId, cameraId, installation, bindingId, unitId,
                            reference, exact.CaptureId, exact.ArtifactId, user, credential, cancellationToken).ConfigureAwait(false);
                        if (observations is null) return null;
                        result = result with { Reconciliation = observations };
                        if (observations.Observations.Count > 0 && observations.Observations[0].State == "Available")
                        {
                            // Producer CaptureId is CentralFrame.FrameId; the archive route takes CentralFrame.Id.
                            // Resolve this through the exact artifact and retained binding, never by assuming UUID equality.
                            var capture = await db.CentralArtifacts.AsNoTracking().Where(a =>
                                a.DevicePublicId == binding.InstallationPublicId && a.ArtifactId == exact.ArtifactId && a.Frame != null &&
                                a.Frame.FrameId == exact.CaptureId && a.Frame.ObservatoryId == observatoryId &&
                                a.Frame.RegistrationId == binding.RegistrationId && a.Frame.DevicePublicId == binding.InstallationPublicId &&
                                a.Frame.LogicalCameraInstallationId == installation && a.Frame.LogicalCameraInstallation!.LogicalCameraId == cameraId)
                                .Select(a => (Guid?)a.Frame!.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                            result = result with { ViewerCapture = capture };
                        }
                    }
                    if (envelope.Execution is { } execution)
                    {
                        var nodeAfter = Position("nodes", query.NodesPage).Name;
                        var nodes = execution.Nodes.OrderBy(n => n.NodeId, StringComparer.Ordinal)
                            .Where(n => nodeAfter is null || string.CompareOrdinal(n.NodeId, nodeAfter) > 0).Take(PageSize + 1).ToArray();
                        var summaries = nodes.Take(PageSize).Select(n => new ImportedNodeSummary(n.NodeId, n.Required, n.PlanSha256, n.Status, n.ReasonCode)).ToArray();
                        result = result with
                        {
                            Envelope = envelope with { Execution = execution with { Nodes = [] } },
                            Nodes = new(summaries, Next("nodes", 0, summaries.LastOrDefault()?.Id, nodes.Length > PageSize))
                        };
                        if (query.Node is { } nodeId)
                        {
                            var node = execution.Nodes.SingleOrDefault(n => n.NodeId == nodeId);
                            if (node is null) throw new ArgumentException("Node unavailable.");
                            var attemptAfter = Position("attempts", query.AttemptsPage).Position;
                            var inputAfter = Position("inputs", query.InputsPage).Position;
                            var outputAfter = Position("outputs", query.OutputsPage).Position;
                            var attempts = Page(node.Attempts.OrderBy(a => a.AttemptNumber).Where(a => a.AttemptNumber > attemptAfter).Take(PageSize + 1).ToArray(), "attempts", a => a.AttemptNumber);
                            var inputs = Page(node.Inputs.OrderBy(i => i.Ordinal).Where(i => i.Ordinal > inputAfter).Take(PageSize + 1).ToArray(), "inputs", i => i.Ordinal);
                            var outputs = Page(node.Outputs.OrderBy(o => o.Ordinal).Where(o => o.Ordinal > outputAfter).Take(PageSize + 1).ToArray(), "outputs", o => o.Ordinal);
                            result = result with
                            {
                                Node = node with { Attempts = [.. attempts.Items.Select(a => a with { LeaseOwner = null })], Inputs = [.. inputs.Items], Outputs = [.. outputs.Items] },
                                AttemptsNext = attempts.NextCursor,
                                InputsNext = inputs.NextCursor,
                                OutputsNext = outputs.NextCursor
                            };
                        }
                        if (query.Central is { } centralId)
                        {
                            result = result with
                            {
                                CentralRequested = true,
                                Central = await central.GetExecutionAsync(principal, observatoryId, cameraId,
                                centralId, PageSize, query.CentralNodesPage, cancellationToken).ConfigureAwait(false)
                            };
                        }
                    }
                    if (envelope.Availability is { } availability)
                    {
                        // The canonical array index is the identity within this sealed report, not a production ordinal.
                        var start = Position("availability", query.NodesPage).Position;
                        var rows = availability.Observations.Select((value, index) => (value, index))
                            .Where(item => item.index > start).Take(PageSize + 1).ToArray();
                        result = result with
                        {
                            Envelope = envelope with { Availability = availability with { Observations = [] } },
                            Availability = new(rows.Take(PageSize).Select(item => item.value).ToArray(),
                                Next("availability", rows.Take(PageSize).LastOrDefault().index, null, rows.Length > PageSize))
                        };
                    }
                }
            }
            else if (query.Unit is not null) throw new ArgumentException("Select a binding.");
        }
        else if (query.Binding is not null || query.Unit is not null) throw new ArgumentException("Select an installation.");
        cancellationToken.ThrowIfCancellationRequested();
        // Imported legacy reads authorize before selecting; the composition also rechecks after its last read.
        return await CanReadAsync(user, observatoryId, cameraId, cancellationToken).ConfigureAwait(false) ? result : null;
    }

    private Task<bool> CanReadAsync(string user, Guid observatory, Guid camera, CancellationToken token) =>
        db.LogicalCameras.AsNoTracking().AnyAsync(c => c.Id == camera && c.ObservatoryId == observatory &&
            ObservatoryMembershipAccess.ForUser(db, user).Any(m => m.ObservatoryId == observatory), token);

    internal static bool Matches(ExecutionEvidenceUnitAssociationView association, ExecutionEvidenceEnvelopeV1 envelope) =>
        association.OriginIdentitySha256 == envelope.Origin.IdentitySha256 && association.OriginInstallationId == envelope.Origin.OriginInstallationId &&
        association.AgentInstanceId == envelope.Origin.AgentInstanceId && association.Unit.EvidenceId == envelope.EvidenceId &&
        association.Unit.OriginSequence == envelope.OriginSequence && association.Unit.PayloadSha256 == envelope.PayloadSha256 &&
        association.Unit.Kind == envelope.Kind.ToString();

    internal static bool Matches(ExecutionEvidenceArtifactView view, ExecutionEvidenceArtifactReferenceV1 reference) =>
        view.ArtifactId == reference.ArtifactId && view.CaptureId == reference.CaptureId &&
        view.OutputIdentitySha256 == reference.OutputIdentitySha256 && view.PayloadSha256 == reference.PayloadSha256 &&
        view.DescriptorSha256 == reference.DescriptorSha256 && view.PayloadLength == reference.PayloadLength &&
        view.MediaType == reference.MediaType && view.Role == reference.Role?.ToString() && view.Variant == reference.Variant;

    private PageCursor Decode(PageCursor selection, string? encoded)
    {
        if (encoded is null) return selection;
        if (encoded.Length > 4096) throw new ArgumentException("Invalid continuation.");
        try
        {
            var cursor = JsonSerializer.Deserialize<PageCursor>(protector.Unprotect(encoded));
            if (cursor is null || cursor.Position < -1 || cursor with { Position = -1, Name = null } != selection)
                throw new ArgumentException("Continuation selection changed.");
            return cursor;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        { throw new ArgumentException("Invalid continuation.", exception); }
    }

    private sealed record PageCursor(string User, Guid? Credential, Guid Observatory, Guid Camera, string Collection,
        Guid? Installation, Guid? Binding, long? Unit, string? Node, string? Kind, string? Origin, long Position = -1, string? Name = null);
}
