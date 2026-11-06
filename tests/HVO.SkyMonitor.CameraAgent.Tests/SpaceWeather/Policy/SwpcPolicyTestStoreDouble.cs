using System.Collections.Immutable;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;

// Test double only: no persistence, notice merging, retention or freshness implementation.
internal sealed class SwpcPolicyTestStoreDouble : ISwpcStore
{
    private readonly Dictionary<Guid, SwpcAttemptStart> starts = [];
    private readonly Dictionary<Guid, SwpcWriteResult> completed = [];
    private readonly Dictionary<Guid, SwpcWriteResult> haltCommands = [];
    public SwpcStoreView View { get; set; } = EmptyView();
    public List<SwpcAttemptStart> Begins { get; } = [];
    public List<SwpcAttemptCompletion> Completions { get; } = [];
    public List<SwpcHaltCommand> Halts { get; } = [];
    public List<SwpcDiagnosticsQuery> Queries { get; } = [];
    public List<SwpcJournalEntry> Journal { get; } = [];
    public int HealthReads { get; private set; }
    public List<string> Calls { get; } = [];
    public List<DateTimeOffset> Reads { get; } = [];
    public Action<SwpcAttemptStart>? BeforeBegin { get; set; }
    public Func<SwpcAttemptCompletion, CancellationToken, ValueTask>? BeforeCompleteAsync { get; set; }
    public int BeginConflicts { get; set; }
    public int CompleteConflicts { get; set; }
    public SwpcWriteStatus? BeginStatus { get; set; }
    public SwpcWriteStatus? CompleteStatus { get; set; }
    public int? HealthIncompleteOverride { get; set; }
    public Func<SwpcAttemptCompletion, Exception?>? CompletionFailure { get; set; }
    public Exception? ProjectionFailure { get; set; }
    public Func<SwpcDiagnosticsQuery, SwpcDiagnosticsPage>? DiagnosticsPage { get; set; }
    public SwpcStoreView? AfterMerge { get; set; }
    public ImmutableHashSet<SwpcMessageNode> Touched { get; set; } = [];
    public ImmutableArray<string> StoreDiagnostics { get; set; } = [];
    public ImmutableArray<SwpcAttentionEvent> ReplayAttention { get; set; } = [];
    public ImmutableArray<SwpcAttentionEvent>? AppliedAttentionOverride { get; set; }
    public bool Incomplete => starts.Keys.Except(completed.Keys).Any();

    public static SwpcStoreView EmptyView() => new(0,
        Enum.GetValues<SwpcProduct>().ToImmutableDictionary(p => p, _ => new SwpcStoredProduct(null,
            new(null, null), null, null, null, new(null, null, false, 0, 0, 0, null), null, SwpcFreshness.None)),
        null, new([], [], [], ImmutableDictionary<SwpcMessageNode, int>.Empty, ImmutableDictionary<SwpcMessageNode, SwpcAcknowledgement>.Empty));

    public ValueTask InitializeAsync(CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask<SwpcStoreView> ReadAsync(DateTimeOffset atUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add("Read");
        Reads.Add(atUtc);
        return ValueTask.FromResult(View);
    }

    public ValueTask<SwpcWriteResult> BeginAttemptAsync(SwpcAttemptStart request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add("Begin");
        Begins.Add(request);
        BeforeBegin?.Invoke(request);
        if (starts.ContainsKey(request.AttemptId)) return Write(SwpcWriteStatus.Replayed);
        if (BeginConflicts-- > 0 || request.ExpectedRevision != View.Revision) return Write(SwpcWriteStatus.Conflict);
        if (BeginStatus is { } status) return Write(status);
        if (Incomplete) return Write(SwpcWriteStatus.Conflict);
        starts.Add(request.AttemptId, request);
        var product = View.Products[request.Product] with { LastAttemptUtc = request.StartedUtc, Checkpoint = request.CheckpointAfterStart };
        View = View with { Revision = View.Revision + 1, Products = View.Products.SetItem(request.Product, product) };
        Journal.Add(new(Journal.Count + 1, request.AttemptId, request.Product, request.Trigger, request.StartedUtc,
            null, null, null, null, null, null, null, []));
        return Write(SwpcWriteStatus.Applied);
    }

    /// <summary>
    /// R2: candidate-less completions project over Before for both views with empty
    /// Touched; configured merge/touched values apply only when a candidate is present.
    /// </summary>
    public async ValueTask<SwpcWriteResult> CompleteAttemptAsync(SwpcAttemptCompletion request, SwpcCommitProjection project, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add("Complete");
        Completions.Add(request);
        if (BeforeCompleteAsync is not null) await BeforeCompleteAsync(request, ct).ConfigureAwait(false);
        if (CompletionFailure?.Invoke(request) is { } exception) throw exception;
        if (CompleteConflicts-- > 0 || request.ExpectedRevision != View.Revision) return await Write(SwpcWriteStatus.Conflict).ConfigureAwait(false);
        if (CompleteStatus is { } status) return await Write(status, attention: ReplayAttention).ConfigureAwait(false);
        if (completed.TryGetValue(request.AttemptId, out var previous))
            return previous with { Status = SwpcWriteStatus.Replayed, View = View };
        if (!starts.TryGetValue(request.AttemptId, out var start)) return await Write(SwpcWriteStatus.NotFound).ConfigureAwait(false);
        if (request.Candidate is { } candidate)
            Assert.AreEqual(SwpcSnapshotComparison.Classify(View.Products[start.Product].LastKnownGood, candidate), request.Outcome);
        if (ProjectionFailure is { } failure && request.Outcome is not null) throw failure;
        var context = request.Candidate is null
            ? new SwpcCommitContext(request, View, View, ImmutableHashSet<SwpcMessageNode>.Empty)
            : new SwpcCommitContext(request, View, AfterMerge ?? View, Touched);
        var effects = project(context);
        var product = View.Products[start.Product];
        if (request.Outcome is not null)
        {
            product = product with { LastOutcome = request.Outcome, Checkpoint = effects.ProductCheckpoint };
            if (request.Outcome == SwpcFetchOutcome.Updated) product = product with { LastKnownGood = request.Candidate };
            if (request.ValidatorOutcome == SwpcValidatorOutcome.Replaced)
                product = product with { Validators = request.Candidate!.ResponseValidators };
            if (request.Outcome is SwpcFetchOutcome.Updated or SwpcFetchOutcome.Unchanged or SwpcFetchOutcome.NotModified)
                product = product with { LastSuccessUtc = request.RetrievedUtc };
        }
        else
        {
            Assert.AreSame(product.Checkpoint, effects.ProductCheckpoint);
            Assert.IsNull(effects.HaltChange);
            Assert.IsNull(effects.NoticeAttention);
            Assert.IsTrue(effects.ProviderAttention.IsEmpty);
        }
        View = View with { Revision = View.Revision + 1, Products = View.Products.SetItem(start.Product, product) };
        if (effects.HaltChange is { } halt) ApplyHalt(halt);
        if (effects.NoticeAttention is { } attention)
            View = View with { Notices = View.Notices with { RaisedRanks = attention.RaisedRanks } };
        var index = Journal.FindIndex(e => e.AttemptId == request.AttemptId);
        Journal[index] = Journal[index] with
        {
            EndedUtc = request.EndedUtc,
            Outcome = request.Outcome,
            HttpStatus = request.HttpStatus,
            ReasonCode = request.ReasonCode,
            BytesRead = request.BytesRead,
            ValidatorOutcome = request.ValidatorOutcome,
            SnapshotSha256 = request.Candidate?.Sha256,
            Diagnostics = StoreDiagnostics
        };
        var result = new SwpcWriteResult(SwpcWriteStatus.Applied, null, View, 0, StoreDiagnostics,
            AppliedAttentionOverride ?? (effects.NoticeAttention?.Events ?? []).AddRange(effects.ProviderAttention));
        completed.Add(request.AttemptId, result);
        return result;
    }

    /// <summary>
    /// R3: check revision before retained command identity. A replay returns the stored
    /// result with current View and Replayed status, without halt/checkpoint effects or
    /// a revision advance. Each first applied command retains its result by CommandId.
    /// </summary>
    public ValueTask<SwpcWriteResult> ChangeHaltAsync(SwpcHaltCommand request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add("ChangeHalt");
        Halts.Add(request);
        if (request.ExpectedRevision != View.Revision) return Write(SwpcWriteStatus.Conflict);
        if (haltCommands.TryGetValue(request.CommandId, out var previous))
            return ValueTask.FromResult(previous with { Status = SwpcWriteStatus.Replayed, View = View });
        ApplyHalt(request.Change);
        foreach (var (key, checkpoint) in request.Checkpoints)
            View = View with { Products = View.Products.SetItem(key, View.Products[key] with { Checkpoint = checkpoint }) };
        View = View with { Revision = View.Revision + 1 };
        var result = new SwpcWriteResult(SwpcWriteStatus.Applied, null, View, 0, [], []);
        haltCommands.Add(request.CommandId, result);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// O1c: enforces SqliteSwpcStore's Limit bounds of 1–200 and minimum non-null
    /// BeforeSequence of 1 before page overrides. Calls and queries include rejections.
    /// Like SQLite, default paging is descending and exclusive; the last returned Sequence is a
    /// continuation only when another qualifying lower-sequence row remains.
    /// </summary>
    public ValueTask<SwpcDiagnosticsPage> ReadDiagnosticsAsync(SwpcDiagnosticsQuery request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add("ReadDiagnostics");
        Queries.Add(request);
        if (request.Limit is < 1 or > 200 || request.BeforeSequence is < 1)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (DiagnosticsPage is not null) return ValueTask.FromResult(DiagnosticsPage(request));
        var entries = Journal.Where(e => request.BeforeSequence is null || e.Sequence < request.BeforeSequence)
            .OrderByDescending(e => e.Sequence).Take(request.Limit + 1).ToImmutableArray();
        var page = entries.Take(request.Limit).ToImmutableArray();
        return ValueTask.FromResult(new SwpcDiagnosticsPage(page, entries.Length > request.Limit ? page[^1].Sequence : null));
    }

    public ValueTask<SwpcStoreHealth> ReadHealthAsync(DateTimeOffset atUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add("ReadHealth");
        HealthReads++;
        return ValueTask.FromResult(new SwpcStoreHealth(1, View.Revision, ImmutableDictionary<SwpcProduct, SwpcProductHealth>.Empty,
            View.ProviderHalt, 0, 0, 0, Journal.Count, HealthIncompleteOverride ?? starts.Count - completed.Count,
            ImmutableDictionary<string, long>.Empty, Journal.FirstOrDefault()?.StartedUtc));
    }

    public ValueTask<SwpcWriteResult> AcknowledgeAsync(SwpcAcknowledgeCommand request, CancellationToken ct) => throw new AssertFailedException("S2 never acknowledges notices.");
    public ValueTask<SwpcPurgeResult> ApplyRetentionAsync(DateTimeOffset atUtc, CancellationToken ct) => throw new AssertFailedException("S2 never owns retention.");
    public void Dispose() { }

    private void ApplyHalt(SwpcHaltChange halt)
    {
        View = halt.Product is { } p
            ? View with { Products = View.Products.SetItem(p, View.Products[p] with { ProductHalt = halt.Value }) }
            : View with { ProviderHalt = halt.Value };
    }

    private ValueTask<SwpcWriteResult> Write(SwpcWriteStatus status, ImmutableArray<SwpcAttentionEvent> attention = default) =>
        ValueTask.FromResult(new SwpcWriteResult(status, null, View, 0, [], attention.IsDefault ? [] : attention));
}
