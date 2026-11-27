using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Offline qualification command. The CameraAgent host and its workers are never started.</summary>
public static class SiloFixtureImportCommand
{
    public const string Argument = "--silo-fixture-history";

    public static Task<int> RunAsync(string[] args, Func<IConfiguration> readTargetConfiguration, TextWriter output,
        CancellationToken cancellationToken) =>
        RunAsync(args, readTargetConfiguration, output, static settings => new SiloInstalledStore(settings), cancellationToken);

    internal static async Task<int> RunAsync(string[] args, Func<IConfiguration> readTargetConfiguration, TextWriter output,
        Func<SiloInstalledSettings, SiloInstalledStore> createInstalledStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readTargetConfiguration);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            var report = await RunCoreAsync(args, readTargetConfiguration, createInstalledStore, cancellationToken).ConfigureAwait(false);
            await WriteAsync(output, report).ConfigureAwait(false);
            return report.ExitCode;
        }
        catch (Exception exception) when (IsCommandFailure(exception))
        {
            // Keep configuration paths, credential material and connection exception details out of the operator report.
            await WriteAsync(output, new
            {
                format = SiloFixtureImport.ReportFormat,
                status = "refused",
                reason = exception is SiloConflictException conflict ? conflict.Code : FailReason(exception)
            }).ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<SiloFixtureReport> RunCoreAsync(string[] args, Func<IConfiguration> readTargetConfiguration,
        Func<SiloInstalledSettings, SiloInstalledStore> createInstalledStore, CancellationToken cancellationToken)
    {
        var mode = args switch
        {
            [Argument, "inventory", _, _] => SiloFixtureMode.Inventory,
            [Argument, "dry-run", _, _, _] => SiloFixtureMode.DryRun,
            [Argument, "publish", _, _, _, var digest] when SiloHash.IsValid(digest) => SiloFixtureMode.Publish,
            _ => throw new SiloConflictException("fixture-invalid-arguments", "Expected inventory, dry-run or publish with its required arguments.")
        };
        using var source = new ConfinedReadDirectory(args[2]);
        var clock = TimeProvider.System;
        var inventory = await SiloFixtureInventory.ReadAsync(source, args[3], clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (mode == SiloFixtureMode.Inventory)
            return await new SiloFixtureImport(source, inventory).RunAsync(mode, null, null, cancellationToken).ConfigureAwait(false);
        if (mode == SiloFixtureMode.Publish && inventory.SelectionSha256 != args[5])
            throw new SiloConflictException("fixture-selection-changed", "The selection differs from the reviewed dry run.");
        if (!DurableSync.SupportsDirectorySync)
            throw new PlatformNotSupportedException("Target qualification requires Linux filesystem identity and directory durability.");

        var configuration = readTargetConfiguration();
        var options = configuration.GetSection("CameraAgent").Get<CameraAgentHostOptions>()
            ?? throw new InvalidOperationException("CameraAgent target configuration is required.");
        var settings = SiloInstalledSettings.Read(configuration.GetSection("CameraAgent:Silo"));
        var importer = new SiloFixtureImport(source, inventory.WithTargetRetention(settings));
        var sourceRoot = PhysicalRoot.Open(args[2]);
        var stateRoot = PhysicalRoot.Open(Path.GetFullPath(options.RawIngressRoot));
        var storeRoot = PhysicalRoot.Open(args[4]);
        using var stateDirectory = new ConfinedReadDirectory(stateRoot.Path);
        using var storeDirectory = new ConfinedReadDirectory(storeRoot.Path);
        var sourceLocation = source.ReadLinuxLocation();
        var stateLocation = stateDirectory.ReadLinuxLocation();
        var storeLocation = storeDirectory.ReadLinuxLocation();
        RequireDisjoint(sourceRoot, sourceLocation, stateRoot, stateLocation);
        RequireDisjoint(sourceRoot, sourceLocation, storeRoot, storeLocation);
        RequireDisjoint(stateRoot, stateLocation, storeRoot, storeLocation);
        if (!Directory.Exists(storeRoot.Resolve(settings.Binding.Bucket)))
            throw new SiloConflictException("fixture-silo-volume-mismatch", "The expected bucket directory is absent from the Silo data volume.");
        using var bucket = new ConfinedReadDirectory(storeRoot.Resolve(settings.Binding.Bucket));
        if (bucket.ReadLinuxLocation().MountId != storeLocation.MountId)
            throw new SiloConflictException("fixture-silo-volume-mismatch", "The Silo bucket is on a separate mount from its installed data root.");

        var dataRootIdentity = CameraAgentSiloBinding.LinuxDataRootIdentity(
            storeLocation.DeviceMajor, storeLocation.DeviceMinor, storeLocation.Inode);
        using var installed = createInstalledStore(settings);
        SiloFixtureCapacity ReadCapacity() =>
            new(stateDirectory.ReadLinuxAvailableBytes(), storeDirectory.ReadLinuxAvailableBytes());

        const string relativeSiloState = ".silo";
        var statePath = stateRoot.Resolve(relativeSiloState);
        var selectedGroups = inventory.Records.Where(static row => row.Status == "selected")
            .Select(static row => row.GroupId!).ToHashSet(StringComparer.Ordinal);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        using var temp = new ConfinedReadDirectory(tempRoot);
        var tempLocation = temp.ReadLinuxLocation();
        if (sourceLocation.Contains(tempLocation) || stateLocation.Contains(tempLocation) || storeLocation.Contains(tempLocation) ||
            sourceRoot.IsInside(tempRoot) || stateRoot.IsInside(tempRoot) || storeRoot.IsInside(tempRoot))
            throw new SiloConflictException("fixture-overlapping-roots", "The inspection scratch must be outside all fixture roots.");

        var scratch = Directory.CreateTempSubdirectory("silo-fixture-inspect-");
        try
        {
            var target = await InspectTargetAsync(cancellationToken).ConfigureAwait(false);
            return mode == SiloFixtureMode.DryRun
                ? await ForecastSelectionAsync(target).ConfigureAwait(false)
                : await PublishSelectionAsync(target.Head is not null).ConfigureAwait(false);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        string StateDatabasePath(string file)
        {
            var path = stateRoot.Resolve(Path.Combine(relativeSiloState, file));
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                stateRoot.Verify(path + suffix, "read-fixture-target-history");
            return path;
        }

        async ValueTask<SiloFixtureTarget> InspectTargetAsync(CancellationToken token)
        {
            await RequireFirstBootPauseAsync(options, stateRoot, scratch.FullName, clock, token).ConfigureAwait(false);
            var path = StateDatabasePath(SiloStateRoot.PublicationDatabaseFileName);
            var target = await new SiloPublicationState(path, settings.Binding, clock)
                .InspectFixtureTargetAsync(scratch.FullName, selectedGroups, token).ConfigureAwait(false);
            await installed.VerifyAsync(token, dataRootIdentity).ConfigureAwait(false);
            await RequireStoreHistoryAsync(installed.Store, settings.Binding, target.Head, target.Pending, token).ConfigureAwait(false);
            return target;
        }

        async ValueTask<SiloFixtureReport> ForecastSelectionAsync(SiloFixtureTarget target)
        {
            var metadata = new SiloObjectSpool(stateRoot, Path.Combine(relativeSiloState, SiloStateRoot.SpoolDirectoryName));
            if (await SiloJournalWriter.CanRecoverFirstGenerationAsync(target.Head, target.Pending, metadata, settings.Binding,
                    SiloInstalledStorage.InitialIdentityFloors, cancellationToken).ConfigureAwait(false))
                target = target with { Pending = null };

            SiloFilePayloadSpool.Inspection InspectSpool() => SiloFilePayloadSpool.Inspect(stateRoot, relativeSiloState,
                settings.MaximumStagedBytes, settings.MaximumStagedFiles);
            SiloFixtureReport? report = null;
            if (target.Receipts.Values.Any(static receipt => receipt.State == SiloStagedState.Visible))
            {
                // The standard reopen verifier records probe outcomes. Run it on a scratch copy only.
                await SiloSqlite.InspectAsync(StateDatabasePath(SiloStateRoot.IndexDatabaseFileName), SiloCatalogIndex.Format,
                    SiloPublicationState.BindingMetadata(settings.Binding), scratch.FullName, cancellationToken,
                    inspectCopy: async (copy, token) =>
                    {
                        using var index = new SiloCatalogIndex(copy, settings.Binding, clock);
                        var catalog = new SiloCatalog(index, new SiloStoreSet(installed.Store), SiloNoAuthorityEvidence.Instance);
                        report = await importer.RunForecastAsync(target, catalog, installed.Store, clock, ReadCapacity, InspectSpool, token)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }
            return report ?? await importer.RunForecastAsync(target, null, installed.Store, clock, ReadCapacity, InspectSpool, cancellationToken)
                .ConfigureAwait(false);
        }

        async ValueTask<SiloFixtureReport> PublishSelectionAsync(bool hasWriterState)
        {
            SiloInstalledStorage? storage = null;
            try
            {
                return await importer.RunAsync(mode, ReadCapacity, PublishAsync, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                storage?.Dispose();
            }

            async ValueTask<bool> PublishAsync(SiloGroupDraft draft,
                Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open, Action requireHeadroom, CancellationToken token)
            {
                if (storage is null)
                {
                    if (!hasWriterState)
                        requireHeadroom();
                    using var currentStateDirectory = new ConfinedReadDirectory(stateRoot.Path);
                    if (currentStateDirectory.ReadLinuxLocation() != stateLocation)
                        throw new SiloConflictException("fixture-target-changed", "The CameraAgent State root changed during qualification.");

                    var lease = SiloStateRoot.Acquire(statePath, prepareSpool: false);
                    try
                    {
                        // A writer could have finished between the initial inspection and acquisition of the lease.
                        await InspectTargetAsync(token).ConfigureAwait(false);
                        storage = new(settings, OptionsFactory.Create(options), clock, faults: null, decorateReadStore: null,
                            installedStore: createInstalledStore(settings), payloadSpool: null, fixtureLease: lease);
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                    await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
                }

                return await SiloFixtureImport.PublishPairAsync(storage.State, storage.Publisher, storage.Catalog,
                    draft, open, requireHeadroom, token).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask RequireStoreHistoryAsync(ISiloObjectStore store, SiloStorageBinding binding,
        SiloJournalHead? head, SiloPendingSegment? pending, CancellationToken cancellationToken)
    {
        const string otherHistory = "fixture-target-has-other-history";
        if (head is { } writer && (writer.WriterGeneration != 1 || writer.Sequence > SiloFixtureInventory.MaximumRecords + 1))
            throw new SiloConflictException(otherHistory, "The target does not have a first-generation fixture journal.");
        await foreach (var key in store.ListKeysAsync(SiloObjectNamespace.JournalPrefix, null, cancellationToken).ConfigureAwait(false))
        {
            if (!SiloObjectNamespace.TryParseSegmentKey(key, out var position) || position.Generation != 1 ||
                position.Sequence > (head?.Sequence ?? 0) && position != pending?.Position)
                throw new SiloConflictException(otherHistory, "The endpoint contains journal history outside the selected fixtures.");
            if (pending is not null && position == pending.Position)
            {
                var remote = await SiloVerifier.ReadCurrentAsync(store, key, cancellationToken).ConfigureAwait(false);
                if (remote is not { } found || found.Bytes.LongLength != pending.Length || SiloHash.Sha256(found.Bytes) != pending.Sha256)
                    throw new SiloConflictException(otherHistory, "The endpoint's pending journal bytes differ from the fixture writer State.");
            }
        }
        if (head?.Position is { } committed)
        {
            var locator = new SiloExactLocator(binding.StoreId, binding.Bucket, SiloObjectNamespace.SegmentKey(committed), head.VersionId!);
            var bytes = await SiloVerifier.ReadExactMetadataAsync(store, locator, cancellationToken).ConfigureAwait(false);
            if (bytes is null || SiloHash.Sha256(bytes) != head.Sha256)
                throw new SiloConflictException(otherHistory, "The endpoint does not contain the exact committed fixture journal head.");
        }
    }

    private static void RequireDisjoint(PhysicalRoot firstRoot, LinuxDirectoryLocation first,
        PhysicalRoot secondRoot, LinuxDirectoryLocation second)
    {
        if (first.Contains(second) || second.Contains(first) ||
            firstRoot.IsInside(secondRoot.Path) || secondRoot.IsInside(firstRoot.Path))
            throw new SiloConflictException("fixture-overlapping-roots", "The source, CameraAgent State and Silo data roots must be disjoint.");
    }

    private static async Task RequireFirstBootPauseAsync(CameraAgentHostOptions options,
        PhysicalRoot stateRoot, string scratchDirectory, TimeProvider clock, CancellationToken cancellationToken)
    {
        const string pauseRequired = "fixture-first-boot-pause-required";
        var seed = options.FirstBootPause.Identity
            ?? throw new SiloConflictException(pauseRequired, "No first-boot pause is configured.");
        string journal;
        try
        {
            journal = stateRoot.Resolve(Path.Combine("journal", "raw-ingress.db"));
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                stateRoot.Verify(journal + suffix, "read-fixture-target-pause");
        }
        catch (FileSystemFaultException exception) when (exception.Kind == FileSystemFaultKind.Containment)
        {
            throw new SiloConflictException(pauseRequired, "The first-boot pause requires confined admission journal files.");
        }
        // A read-only SQLite connection still writes WAL shared-memory read marks. Inspect a private copy instead.
        var copy = Path.Combine(scratchDirectory, "raw-ingress.db");
        File.Delete(copy + "-shm");
        foreach (var suffix in new[] { "", "-wal", "-journal" })
        {
            File.Delete(copy + suffix);
            if (File.Exists(journal + suffix))
                File.Copy(journal + suffix, copy + suffix);
        }
        var control = new SqliteCaptureControlStore(copy, options.RawIngressSqliteBusyTimeoutSeconds, clock);
        var pause = await control.ReadFirstBootPauseAsync(seed, cancellationToken, readOnly: true).ConfigureAwait(false);
        if (!pause.InForce || pause.Resumed || pause.Version != 1 || pause.State != "paused")
            throw new SiloConflictException(pauseRequired, "The disposable target must still be at its first-boot pause.");
    }

    private static bool IsCommandFailure(Exception exception) => exception is
        ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException or
        System.Text.Json.JsonException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or PlatformNotSupportedException or
        Amazon.Runtime.AmazonServiceException or HttpRequestException;

    private static string FailReason(Exception exception) => exception switch
    {
        System.Text.Json.JsonException or InvalidDataException => "fixture-invalid-selection",
        PlatformNotSupportedException => "fixture-platform-unsupported",
        ArgumentException => "fixture-invalid-input",
        IOException or UnauthorizedAccessException => "fixture-source-or-target-unavailable",
        _ => "fixture-target-refused"
    };

    private static Task WriteAsync<T>(TextWriter output, T value) =>
        output.WriteLineAsync(Encoding.UTF8.GetString(SiloJson.Serialize(value)));
}
