using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed record InstallerControlPublicationRefusal(Guid OperationId, int StatusCode, DateTimeOffset Utc);

internal sealed record InstallerControlJournalState(int SchemaVersion, Guid WorkerId, IReadOnlyList<CameraAgentControlReceipt> Receipts,
    IReadOnlyList<Guid> Unpublished, IReadOnlyList<InstallerControlPublicationRefusal>? Refused = null);

/// <summary>Owner-only installer authority, outside CameraAgent's state mounts; synced before acknowledging effects.</summary>
internal sealed class InstallerControlJournal(string path)
{
    private InstallerControlJournalState _state = new(1, Guid.NewGuid(), [], []);
    internal Guid WorkerId => _state.WorkerId;
    internal IReadOnlyList<CameraAgentControlReceipt> Receipts => _state.Receipts;
    internal IReadOnlyList<InstallerControlPublicationRefusal> Refused => _state.Refused ?? [];
    internal bool CanRetainNew => _state.Receipts.Count < 100 || _state.Receipts.Any(item => item.IsTerminal &&
        !_state.Unpublished.Contains(item.Request.OperationId) && !Refused.Any(refusal => refusal.OperationId == item.Request.OperationId));
    internal IEnumerable<CameraAgentControlReceipt> Unpublished => _state.Receipts.Where(item => _state.Unpublished.Contains(item.Request.OperationId));

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(path);
        if (new FileInfo(path).LinkTarget is not null) throw new InstallerException("The installer lifecycle journal must not be a symbolic link.");
        if (!File.Exists(path))
        {
            await WriteAsync(_state).ConfigureAwait(false);
            return;
        }
        if (new FileInfo(path).Length > 1024 * 1024) throw new InstallerException("Installer lifecycle journal exceeds its bound.");
        using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        _state = await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.InstallerControlJournalState,
            cancellationToken).ConfigureAwait(false) ?? throw new InstallerException("Installer lifecycle journal is empty.");
        if (_state.SchemaVersion != 1 || _state.WorkerId == Guid.Empty || _state.Receipts is null || _state.Unpublished is null || _state.Receipts.Count > 100 ||
            _state.Receipts.Any(item => !item.IsValid()) ||
            _state.Receipts.Select(item => item.Request.OperationId).Distinct().Count() != _state.Receipts.Count ||
            _state.Unpublished.Any(id => !_state.Receipts.Any(item => item.Request.OperationId == id)) ||
            Refused.Count > 100 || Refused.Select(item => item.OperationId).Distinct().Count() != Refused.Count ||
            Refused.Any(item => item.StatusCode is < 400 or > 499 || item.Utc == default || item.Utc.Offset != TimeSpan.Zero ||
                !_state.Receipts.Any(receipt => receipt.Request.OperationId == item.OperationId)))
            throw new InstallerException("Installer lifecycle journal is invalid.");
    }

    internal async Task SaveAsync(CameraAgentControlReceipt receipt)
    {
        var retained = _state.Receipts.Where(item => item.Request.OperationId != receipt.Request.OperationId).ToList();
        while (retained.Count >= 100)
        {
            var discard = retained.FindIndex(item => item.IsTerminal && !_state.Unpublished.Contains(item.Request.OperationId) && !Refused.Any(refusal => refusal.OperationId == item.Request.OperationId));
            if (discard < 0) throw new InstallerException("Unpublished lifecycle evidence must be reconciled before accepting more requests.");
            retained.RemoveAt(discard);
        }
        retained.Add(receipt);
        var next = new InstallerControlJournalState(1, _state.WorkerId, retained,
            _state.Unpublished.Append(receipt.Request.OperationId).Distinct().ToArray(), _state.Refused);
        await WriteAsync(next).ConfigureAwait(false);
    }

    internal Task RefuseAsync(Guid operationId, int statusCode, DateTimeOffset utc) => WriteAsync(_state with
    {
        Unpublished = _state.Unpublished.Where(id => id != operationId).ToArray(),
        Refused = Refused.Where(item => item.OperationId != operationId).Append(new(operationId, statusCode, utc)).ToArray()
    });

    internal Task AcknowledgeAsync(Guid operationId) => WriteAsync(_state with
    { Unpublished = _state.Unpublished.Where(id => id != operationId).ToArray() });

    internal async Task RecoverInterruptedAsync(DateTimeOffset utc)
    {
        foreach (var incomplete in _state.Receipts.Where(item => !item.IsTerminal).ToArray())
            await SaveAsync(incomplete with
            {
                Phase = CameraAgentControlPhase.Failed,
                Events = [.. incomplete.Events, new(CameraAgentControlPhase.Failed, utc, "lifecycle.installer-interrupted")]
            })
                .ConfigureAwait(false);
    }

    private async Task WriteAsync(InstallerControlJournalState next)
    {
        await SafeFileSystem.WriteJsonAtomicAsync(path, next, DeploymentJsonContext.Default.InstallerControlJournalState,
            CancellationToken.None).ConfigureAwait(false);
        _state = next;
    }
}
