using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Deployment;

/// <summary>
/// Durable intent and installer receipt projection, separate from pause/resume authority. Only an authenticated
/// installer exchange registers the capability. The installer keeps its authoritative journal outside this store.
/// </summary>
public sealed class CameraAgentControlStore(IOptions<CameraAgentHostOptions> options, TimeProvider timeProvider) : IDisposable
{
    private const int RetainedReceipts = 100;
    private const string RelativePath = ".lifecycle-control/state.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State? _state;
    private Guid _worker;
    private DateTimeOffset _leaseUntil;
    private int _admissionHeld;
    private TaskCompletionSource _admissionReleased = ReleasedSignal();

    public Guid ProcessId { get; } = Guid.NewGuid();
    public bool Available => _worker != Guid.Empty && timeProvider.GetUtcNow() < _leaseUntil;
    public bool AdmissionHeld => Volatile.Read(ref _admissionHeld) != 0;
    public Task WaitForAdmissionReleaseAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _admissionReleased).Task.WaitAsync(cancellationToken);

    private static TaskCompletionSource ReleasedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    public async Task<CameraAgentControlView> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return new(Available, state.Receipts.ToArray());
        }
        finally { _gate.Release(); }
    }

    public async Task<CameraAgentControlReceipt> SubmitAsync(CameraAgentControlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid()) throw new ArgumentException("A valid lifecycle reason and identity are required.", nameof(request));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Receipts.FirstOrDefault(item => item.Request.OperationId == request.OperationId) is { } prior)
            {
                // RequestedUtc belongs to the first durable admission, not to the caller's retry clock.
                if (prior.Request != request with { RequestedUtc = prior.Request.RequestedUtc })
                    throw new InvalidOperationException("The lifecycle command identity has a different payload.");
                return prior;
            }
            if (!Available) throw new InvalidOperationException("The installer lifecycle worker is unavailable.");
            if (state.Receipts.Any(item => !item.IsTerminal))
                throw new InvalidOperationException("An installer lifecycle operation is already pending.");
            var receipt = new CameraAgentControlReceipt(request, CameraAgentControlPhase.Queued,
                [new(CameraAgentControlPhase.Queued, request.RequestedUtc, "lifecycle.queued")]);
            await SaveAsync(new(1, [.. state.Receipts.TakeLast(RetainedReceipts - 1), receipt], null), cancellationToken)
                .ConfigureAwait(false);
            return receipt;
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Receipts.Any(item => item.Request.OperationId == operationId && !item.IsTerminal))
                await SaveAsync(state with { CancelledOperationId = operationId }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Fences new admission during installer-owned drain/restart, including across host startup.</summary>
    public async Task<bool> MayResumeAsync(string actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var active = state.Receipts.FirstOrDefault(item => !item.IsTerminal);
            return active is null || active.Phase == CameraAgentControlPhase.Verifying &&
                active.VerifiedProcessId == ProcessId && state.CancelledOperationId != active.Request.OperationId &&
                active.ResumeRequested &&
                actor == $"installer-lifecycle:{active.Request.OperationId:D}";
        }
        finally { _gate.Release(); }
    }

    /// <summary>Checks a proposed installer update before an endpoint performs any failure-settlement effect.</summary>
    public async Task<bool> ValidateReceiptAsync(CameraAgentControlExchange exchange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (exchange.WorkerId == Guid.Empty || Available && _worker != exchange.WorkerId)
                throw new InvalidOperationException("The installer worker does not own this exchange.");
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (exchange.Receipt is not { } next) return false;
            var prior = state.Receipts.FirstOrDefault(item => item.Request.OperationId == next.Request.OperationId);
            if (prior is null || !IsValidSuccessor(prior, next)) throw new InvalidOperationException("The lifecycle receipt is invalid.");
            return !Same(prior, next);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Called only after installation-token authorization, never by a browser command.</summary>
    public async Task<(CameraAgentControlReceipt? Current, bool Cancelled)> ExchangeAsync(
        CameraAgentControlExchange exchange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (exchange.WorkerId == Guid.Empty) throw new ArgumentException("A worker identity is required.", nameof(exchange));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Available && _worker != exchange.WorkerId)
                throw new InvalidOperationException("Another installer worker holds the lifecycle capability.");
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (exchange.Receipt is { } receipt)
            {
                var prior = state.Receipts.FirstOrDefault(item => item.Request.OperationId == receipt.Request.OperationId);
                if (prior is null || !IsValidSuccessor(prior, receipt))
                    throw new InvalidOperationException("The installer receipt does not match the retained lifecycle request.");
                if (!Same(prior, receipt))
                {
                    state = state with
                    {
                        Receipts = state.Receipts.Select(item => item.Request.OperationId == receipt.Request.OperationId ? receipt : item).ToArray()
                    };
                    await SaveAsync(state, cancellationToken).ConfigureAwait(false);
                }
            }
            _worker = exchange.WorkerId;
            _leaseUntil = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
            var pending = state.Receipts.FirstOrDefault(item => !item.IsTerminal);
            return (pending, pending is not null && state.CancelledOperationId == pending.Request.OperationId);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> MayRestartAsync(CameraAgentControlRestart command, long captureVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return Available && _worker == command.WorkerId && state.CancelledOperationId != command.OperationId &&
                command.ExpectedCaptureVersion == captureVersion && state.Receipts.Any(item =>
                    item.Request.OperationId == command.OperationId && item.Phase == CameraAgentControlPhase.Restarting &&
                    item.PausedVersion == captureVersion);
        }
        finally { _gate.Release(); }
    }

    private static bool Same(CameraAgentControlReceipt left, CameraAgentControlReceipt right) =>
        JsonSerializer.Serialize(left, Json) == JsonSerializer.Serialize(right, Json);

    private static bool IsValidSuccessor(CameraAgentControlReceipt prior, CameraAgentControlReceipt next) =>
        next.IsValid() && next.Request == prior.Request &&
        (!prior.ResumeRequested || next.ResumeRequested) &&
        (prior.OriginalProcessId is null || next.OriginalProcessId == prior.OriginalProcessId) &&
        (prior.PausedVersion is null || next.PausedVersion == prior.PausedVersion) &&
        (prior.VerificationDeadlineUtc is null || next.VerificationDeadlineUtc == prior.VerificationDeadlineUtc) && next.Events is { Count: > 0 and <= 10 } &&
        Enum.IsDefined(next.Phase) && next.Events.Count >= prior.Events.Count &&
        next.Events.Take(prior.Events.Count).SequenceEqual(prior.Events) && next.Events[^1].Phase == next.Phase &&
        next.Events.All(item => Enum.IsDefined(item.Phase) && item.Utc != default && item.Utc.Offset == TimeSpan.Zero &&
            item.Code is { Length: > 0 and <= 96 } && item.Code.All(character => char.IsAsciiLetterLower(character) || character is '.' or '-')) &&
        (!prior.IsTerminal || Same(prior, next));

    private async Task<State> LoadAsync(CancellationToken cancellationToken)
    {
        if (_state is not null) return _state;
        var root = Root();
        var path = root.Resolve(RelativePath);
        if (!File.Exists(path)) return _state = new(1, [], null);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Lifecycle state exceeds its bound.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var state = await JsonSerializer.DeserializeAsync<State>(stream, Json, cancellationToken).ConfigureAwait(false);
        if (state is null || state.SchemaVersion != 1 || state.Receipts is null || state.Receipts.Length > RetainedReceipts ||
            state.Receipts.Any(item => item is null || !item.IsValid()) ||
            state.Receipts.Count(item => !item.IsTerminal) > 1 ||
            state.Receipts.Select(item => item.Request.OperationId).Distinct().Count() != state.Receipts.Length)
            throw new InvalidDataException("Lifecycle state is invalid.");
        ApplyState(state);
        return state;
    }

    private async Task SaveAsync(State state, CancellationToken cancellationToken)
    {
        await AtomicPublisher.PublishAsync(Root(), RelativePath, PublishMode.Replace, async (stream, token) =>
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(((FileStream)stream).SafeFileHandle,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await JsonSerializer.SerializeAsync(stream, state, Json, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        ApplyState(state);
    }

    private void ApplyState(State state)
    {
        var held = state.Receipts.Any(item => !item.IsTerminal && item.Phase != CameraAgentControlPhase.Queued);
        if (held && !AdmissionHeld)
        {
            Volatile.Write(ref _admissionReleased, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _admissionHeld, 1);
        }
        else if (!held && AdmissionHeld)
        {
            Volatile.Write(ref _admissionHeld, 0);
            _admissionReleased.TrySetResult();
        }
        _state = state;
    }

    private PhysicalRoot Root()
    {
        var path = Path.GetFullPath(options.Value.RawIngressRoot);
        Directory.CreateDirectory(path);
        return PhysicalRoot.Open(path);
    }

    public void Dispose() => _gate.Dispose();
    private sealed record State(int SchemaVersion, CameraAgentControlReceipt[] Receipts, Guid? CancelledOperationId);
}
