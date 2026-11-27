using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Offline qualification command. The CameraAgent host and its workers are never started.</summary>
public static class SiloFixtureImportCommand
{
    public const string Argument = "--silo-fixture-history";

    public static async Task<int> RunAsync(string[] args, Func<IConfiguration> readTargetConfiguration, TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readTargetConfiguration);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            var report = await RunCoreAsync(args, readTargetConfiguration, cancellationToken).ConfigureAwait(false);
            await WriteAsync(output, report).ConfigureAwait(false);
            return report.Records.Any(static row => row.Status == "failed" ||
                row.Status == "skipped" && row.Reason != "already-imported")
                ? 2 : 0;
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
        CancellationToken cancellationToken)
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
        var importer = new SiloFixtureImport(source, inventory);
        if (mode == SiloFixtureMode.Inventory)
            return await importer.RunAsync(mode, null, null, cancellationToken).ConfigureAwait(false);
        if (mode == SiloFixtureMode.Publish && inventory.SelectionSha256 != args[5])
            throw new SiloConflictException("fixture-selection-changed", "The selection differs from the reviewed dry run.");

        var configuration = readTargetConfiguration();
        var options = configuration.GetSection("CameraAgent").Get<CameraAgentHostOptions>()
            ?? throw new InvalidOperationException("CameraAgent target configuration is required.");
        var settings = SiloInstalledSettings.Read(configuration.GetSection("CameraAgent:Silo"));
        var stateRoot = PhysicalRoot.Open(Path.GetFullPath(options.RawIngressRoot));
        var storeRoot = PhysicalRoot.Open(args[4]);
        var sourceRoot = PhysicalRoot.Open(args[2]);
        RequireDisjoint(sourceRoot, stateRoot);
        RequireDisjoint(sourceRoot, storeRoot);
        RequireDisjoint(stateRoot, storeRoot);
        // Require the matching installed local bucket on the mounted data volume, even during a dry run.
        if (!Directory.Exists(storeRoot.Resolve(settings.Binding.Bucket)))
            throw new SiloConflictException("fixture-silo-volume-mismatch", "The expected bucket directory is absent from the Silo data volume.");

        var capacity = new FileSystemStorageCapacityProvider();
        SiloFixtureCapacity ReadCapacity() =>
            new(capacity.GetCapacity(stateRoot.Path).AvailableBytes, capacity.GetCapacity(storeRoot.Path).AvailableBytes);

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
            Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open, CancellationToken token)
        {
            if (storage is null)
            {
                await RequireFirstBootPauseAsync(options, stateRoot, clock, token).ConfigureAwait(false);
                storage = new(settings, OptionsFactory.Create(options), clock);
                // Check the pause again while holding the installed Silo writer lease.
                await RequireFirstBootPauseAsync(options, stateRoot, clock, token).ConfigureAwait(false);
                await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
                var selectedGroups = inventory.Records.Where(static row => row.Status == "selected")
                    .Select(static row => row.GroupId!).ToHashSet(StringComparer.Ordinal);
                await storage.State.RequireFixtureTargetAsync(selectedGroups, token).ConfigureAwait(false);
            }

            return await SiloFixtureImport.PublishPairAsync(storage.State, storage.Publisher, storage.Catalog, draft, open, token)
                .ConfigureAwait(false);
        }
    }

    private static void RequireDisjoint(PhysicalRoot first, PhysicalRoot second)
    {
        if (first.IsInside(second.Path) || second.IsInside(first.Path))
            throw new SiloConflictException("fixture-overlapping-roots", "The source, CameraAgent State and Silo data roots must be disjoint.");
    }

    private static async Task RequireFirstBootPauseAsync(CameraAgentHostOptions options,
        PhysicalRoot stateRoot, TimeProvider clock, CancellationToken cancellationToken)
    {
        var seed = options.FirstBootPause.Identity
            ?? throw new SiloConflictException("fixture-first-boot-pause-required", "No first-boot pause is configured.");
        var journal = stateRoot.Resolve(Path.Combine("journal", "raw-ingress.db"));
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            stateRoot.Verify(journal + suffix, "read-fixture-target-pause");
        var control = new SqliteCaptureControlStore(journal, options.RawIngressSqliteBusyTimeoutSeconds, clock);
        var pause = await control.ReadFirstBootPauseAsync(seed, cancellationToken, readOnly: true).ConfigureAwait(false);
        if (!pause.InForce || pause.Resumed || pause.Version != 1 || pause.State != "paused")
            throw new SiloConflictException("fixture-first-boot-pause-required", "The disposable target must still be at its first-boot pause.");
    }

    private static bool IsCommandFailure(Exception exception) => exception is
        ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException or
        System.Text.Json.JsonException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or PlatformNotSupportedException;

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
