using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed record InstallerControlJournalState(int SchemaVersion, IReadOnlyList<CameraAgentControlReceipt> Receipts,
    IReadOnlyList<Guid> Unpublished);

/// <summary>Owner-only installer authority, outside CameraAgent's state mounts; synced before acknowledging effects.</summary>
internal sealed class InstallerControlJournal(string path)
{
    private InstallerControlJournalState _state = new(1, [], []);
    internal IReadOnlyList<CameraAgentControlReceipt> Receipts => _state.Receipts;
    internal IEnumerable<CameraAgentControlReceipt> Unpublished => _state.Receipts.Where(item => _state.Unpublished.Contains(item.Request.OperationId));

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(path);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InstallerException("Installer lifecycle journal exceeds its bound.");
        using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        _state = await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.InstallerControlJournalState,
            cancellationToken).ConfigureAwait(false) ?? throw new InstallerException("Installer lifecycle journal is empty.");
        if (_state.SchemaVersion != 1 || _state.Receipts is null || _state.Unpublished is null || _state.Receipts.Count > 100 ||
            _state.Receipts.Any(item => !item.IsValid()) ||
            _state.Receipts.Select(item => item.Request.OperationId).Distinct().Count() != _state.Receipts.Count ||
            _state.Unpublished.Any(id => !_state.Receipts.Any(item => item.Request.OperationId == id)))
            throw new InstallerException("Installer lifecycle journal is invalid.");
    }

    internal async Task SaveAsync(CameraAgentControlReceipt receipt)
    {
        var retained = _state.Receipts.Where(item => item.Request.OperationId != receipt.Request.OperationId).ToList();
        while (retained.Count >= 100)
        {
            var discard = retained.FindIndex(item => item.IsTerminal && !_state.Unpublished.Contains(item.Request.OperationId));
            if (discard < 0) throw new InstallerException("Unpublished lifecycle evidence must be reconciled before accepting more requests.");
            retained.RemoveAt(discard);
        }
        retained.Add(receipt);
        var next = new InstallerControlJournalState(1, retained,
            _state.Unpublished.Append(receipt.Request.OperationId).Distinct().ToArray());
        await WriteAsync(next).ConfigureAwait(false);
    }

    internal Task AcknowledgeAsync(Guid operationId) => WriteAsync(_state with
    { Unpublished = _state.Unpublished.Where(id => id != operationId).ToArray() });

    internal async Task RecoverInterruptedAsync(DateTimeOffset utc)
    {
        foreach (var incomplete in _state.Receipts.Where(item => !item.IsTerminal).ToArray())
            await SaveAsync(incomplete with { Phase = CameraAgentControlPhase.Failed,
                Events = [.. incomplete.Events, new(CameraAgentControlPhase.Failed, utc, "lifecycle.installer-interrupted")] })
                .ConfigureAwait(false);
    }

    private async Task WriteAsync(InstallerControlJournalState next)
    {
        await SafeFileSystem.WriteJsonAtomicAsync(path, next, DeploymentJsonContext.Default.InstallerControlJournalState,
            CancellationToken.None).ConfigureAwait(false);
        _state = next;
    }
}
