using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>The process owner of the installed writer, local publication State, and catalog.</summary>
internal sealed class SiloInstalledStorage : ICameraAgentSiloVerification, IDisposable
{
    private readonly SiloInstalledStore _installed;
    private readonly SiloStateRoot _root;
    private readonly PhysicalRoot _rawRoot;
    private readonly SiloJournalWriter _journal;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly bool _publicationStateExisted;
    private bool _localInitialized;
    private bool _bindingVerified;
    private int _disposed;

    public SiloInstalledStorage(SiloInstalledSettings settings, IOptions<CameraAgentHostOptions> options, TimeProvider clock,
        ISiloFaults? faults = null, Func<ISiloObjectStore, ISiloObjectStore>? decorateReadStore = null)
        : this(settings, options, clock, faults, decorateReadStore, installedStore: null, payloadSpool: null)
    {
    }

    internal SiloInstalledStorage(SiloInstalledSettings settings, IOptions<CameraAgentHostOptions> options, TimeProvider clock,
        ISiloFaults? faults, Func<ISiloObjectStore, ISiloObjectStore>? decorateReadStore, SiloInstalledStore? installedStore,
        ISiloPayloadSpool? payloadSpool)
    {
        var rawRoot = Path.GetFullPath(options.Value.RawIngressRoot);
        Directory.CreateDirectory(rawRoot);
        _rawRoot = PhysicalRoot.Open(rawRoot);
        var statePath = _rawRoot.Resolve(".silo");
        _root = SiloStateRoot.Acquire(statePath);
        try
        {
            Binding = settings.Binding;
            _publicationStateExisted = File.Exists(_root.PublicationDatabasePath);
            Spool = payloadSpool ?? new SiloFilePayloadSpool(_root, settings.MaximumStagedBytes, settings.MaximumStagedFiles);
            _installed = installedStore ?? new(settings);
            State = new(_root.PublicationDatabasePath, Binding, clock);
            Index = new(_root.IndexDatabasePath, Binding, clock);
            Catalog = new(Index, new SiloStoreSet(decorateReadStore?.Invoke(_installed.Store) ?? _installed.Store), SiloNoAuthorityEvidence.Instance, faults);
            _journal = new(State, _root.Spool, _installed.Store, Binding, clock, faults);
            Publisher = new(State, _root.Spool, _journal, Catalog, Spool, Binding, clock, faults);
        }
        catch
        {
            _installed?.Dispose();
            _root.Dispose();
            throw;
        }
    }

    public SiloStorageBinding Binding { get; }

    public SiloCatalog Catalog { get; }

    public SiloPublicationState State { get; }

    public SiloCatalogIndex Index { get; }

    public SiloPublisher Publisher { get; }

    internal SiloJournalWriter ArchiveJournal => _journal;

    public ISiloPayloadSpool Spool { get; }

    internal PhysicalRoot PhysicalState => _root.Root;

    public bool BindingVerified => Volatile.Read(ref _bindingVerified);

    internal FileStream OpenReadScratch()
    {
        var path = _root.Root.Resolve($"read-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            // Silo State requires Linux directory durability. An unlinked scratch file cannot survive process loss.
            File.Delete(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal async ValueTask<long> ReadViewCursorAsync(CancellationToken cancellationToken)
    {
        var path = _root.Root.Resolve("centered-view-cursor");
        if (!File.Exists(path)) return 0;
        if (new FileInfo(path).Length is < 1 or > 19 || !long.TryParse(
            await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), NumberStyles.None, CultureInfo.InvariantCulture,
            out var value) || value < 0)
            throw new InvalidDataException("The Silo centered-view publication cursor is invalid.");
        return value;
    }

    internal async ValueTask WriteViewCursorAsync(long order, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(order.ToString(CultureInfo.InvariantCulture));
        await AtomicPublisher.PublishAsync(_root.Root, "centered-view-cursor", PublishMode.Replace,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verify the installed binding and at most one committed journal version. Tail recovery is separate.</summary>
    public async ValueTask VerifyReadinessAsync(CancellationToken cancellationToken)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _installed.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (!_localInitialized)
            {
                // Do not create a replacement writer DB for a bucket that already contains a journal.
                if (!_publicationStateExisted)
                {
                    await foreach (var _ in _installed.Store.ListKeysAsync(SiloObjectNamespace.JournalPrefix, null, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        throw new SiloConflictException("silo.writer-state-missing", "The installed Silo journal already exists. Recover its writer State before starting CameraAgent.");
                    }
                }
                await State.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await Index.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _localInitialized = true;
            }

            var head = await State.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head.WriterGeneration != 1)
                throw new SiloConflictException("silo.writer-generation-mismatch", "The Silo writer State belongs to a different generation.");
            if (head.Position is { } position)
            {
                var key = SiloObjectNamespace.SegmentKey(position);
                var locator = new SiloExactLocator(Binding.StoreId, Binding.Bucket, key, head.VersionId!);
                var bytes = await SiloVerifier.ReadExactMetadataAsync(_installed.Store, locator, cancellationToken).ConfigureAwait(false);
                if (bytes is null || SiloHash.Sha256(bytes) != head.Sha256)
                    throw new SiloConflictException("silo.writer-head-changed", "The installed Silo store no longer contains the exact committed journal head.");
            }
            else
            {
                await _journal.OpenFirstGenerationAsync(new Dictionary<string, long> { ["capture-sequence"] = 0 }, cancellationToken)
                    .ConfigureAwait(false);
            }
            Volatile.Write(ref _bindingVerified, true);
        }
        catch (Exception exception) when (exception is SiloConflictException or SiloDataException)
        {
            Volatile.Write(ref _bindingVerified, false);
            throw;
        }
        finally
        {
            _initialization.Release();
        }
    }

    public async ValueTask<CameraAgentSiloVerificationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        await VerifyReadinessAsync(cancellationToken).ConfigureAwait(false);
        await VerifyAdmissionJournalAsync(cancellationToken).ConfigureAwait(false);
        var (cursor, cursorSha) = await Index.ReadCursorAsync(cancellationToken).ConfigureAwait(false);
        if (cursor is null)
        {
            var writer = await State.ReadWriterEvidenceAsync(cancellationToken).ConfigureAwait(false);
            if (writer.Head == new SiloJournalPosition(1, 1))
            {
                await Catalog.CatchUpAsync(writer, null, cancellationToken).ConfigureAwait(false);
                (cursor, cursorSha) = await Index.ReadCursorAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (cursor is not { } cutoff || cursorSha is null ||
            await State.CommittedSha256Async(cutoff, cancellationToken).ConfigureAwait(false) != cursorSha)
            throw new SiloConflictException("silo.catalog-recovering", "The Silo catalog has not reached the committed journal head.");

        var journal = new SiloJournalWalker(_installed.Store, Binding, SiloNoAuthorityEvidence.Instance);
        var bound = new SiloJournalBound(true, cutoff, cursorSha, State);
        var end = await journal.WalkAsync(null, null, static _ => ValueTask.FromResult(true), cancellationToken, bound).ConfigureAwait(false);
        if (end.Reason != SiloPrefixEnd.Tail || !bound.IsReachedBy(end.Last, end.LastSha256))
            throw new SiloConflictException("silo.journal-verification-failed", "The Silo journal could not be verified through the catalog cutoff.");

        return await Index.VerifyHistoryAsync(Catalog.Stores, cutoff, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask VerifyAdmissionJournalAsync(CancellationToken cancellationToken)
    {
        var path = _rawRoot.Resolve(Path.Combine("journal", "raw-ingress.db"));
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            _rawRoot.Verify(path + suffix, "silo-verify-admission-journal");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteRawCaptureJournal.ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The Silo admission journal could not be fully verified.", exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Publisher.Dispose();
        _journal.Dispose();
        Index.Dispose();
        _installed.Dispose();
        _initialization.Dispose();
        _root.Dispose();
    }
}
