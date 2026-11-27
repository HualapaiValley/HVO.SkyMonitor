using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task StockSiloPublishesAndReopensAnOfflineHistoryFixtureWithInstalledCredentials()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var root = Directory.CreateTempSubdirectory("silo-history-").FullName;
        try
        {
            await ImportStockHistoryAsync(root, token).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ImportStockHistoryAsync(string root, CancellationToken token)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "snapshot")).FullName;
        var rawRoot = Directory.CreateDirectory(Path.Combine(root, "raw")).FullName;
        var dataRoot = Directory.CreateDirectory(Path.Combine(root, "silo")).FullName;
        var instance = Guid.NewGuid();
        var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance),
            CameraAgentSiloBinding.BucketFor(instance));
        var access = RandomHex(12);
        var secret = RandomHex(24);
        var user = FormattableString.Invariant($"{EffectiveUserId()}:{EffectiveGroupId()}");
        var container = BuildStockContainer(access, secret, "on", dataRoot, user);
        await using var lease = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        using var admin = SiloS3ObjectStore.CreateClient(Endpoint(container), access, secret);
        await CreateVersionedBucketAsync(admin, binding.Bucket, token).ConfigureAwait(false);
        var writer = await CreateWriterAsync(container, binding, token).ConfigureAwait(false);
        using var storeDirectory = new ConfinedReadDirectory(dataRoot);
        var location = storeDirectory.ReadLinuxLocation();
        await InstallBindingAsync(admin, binding, writer.Access, token,
            CameraAgentSiloBinding.LinuxDataRootIdentity(location.DeviceMajor, location.DeviceMinor, location.Inode)).ConfigureAwait(false);
        var pause = Guid.NewGuid();
        var settings = InstalledSettings(binding, Endpoint(container), writer, pause, RandomHex(24));
        settings["CameraAgent:RawIngressRoot"] = rawRoot;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        await FirstBootStateInitializer.InitializeAsync(rawRoot, 1, new(pause), token).ConfigureAwait(false);

        const string selectionFile = "export.selection.json";
        var captured = new DateTimeOffset(2025, 11, 26, 0, 0, 0, TimeSpan.Zero);
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNoAAAAggCBd81ytgAAAABJRU5ErkJggg==");
        var sidecar = SiloJson.Serialize(new SiloFixtureSidecar(SiloFixtureInventory.SidecarFormat, "source-capture",
            "source-camera", "source-image", captured.AddSeconds(-1), captured));
        var selection = SiloJson.Serialize(new SiloFixtureSelection(SiloFixtureInventory.SelectionFormat,
            "source-export", 1024 * 1024, 1024 * 1024, [new("frame", SiloHash.Sha256(image), SiloHash.Sha256(sidecar))]));
        await File.WriteAllBytesAsync(Path.Combine(source, "frame.png"), image, token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(source, "frame.json"), sidecar, token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(source, selectionFile), selection, token).ConfigureAwait(false);

        var arguments = new[] { SiloFixtureImportCommand.Argument, "dry-run", source, selectionFile, dataRoot };
        var copyRoot = Directory.CreateDirectory(Path.Combine(root, "other-silo")).FullName;
        Directory.CreateDirectory(Path.Combine(copyRoot, binding.Bucket));
        var wrongVolume = arguments[..^1].Append(copyRoot).ToArray();
        using var emptyBucket = await RunFixtureCommandAsync(wrongVolume, configuration, token, expectedExitCode: 2).ConfigureAwait(false);
        Assert.AreEqual("fixture-silo-volume-mismatch", emptyBucket.RootElement.GetProperty("reason").GetString());
        foreach (var path in Directory.EnumerateFiles(Path.Combine(dataRoot, binding.Bucket), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(copyRoot, Path.GetRelativePath(dataRoot, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
        Assert.IsTrue(Directory.EnumerateFiles(copyRoot, "*", SearchOption.AllDirectories).Any());
        using var copiedBucket = await RunFixtureCommandAsync(wrongVolume, configuration, token, expectedExitCode: 2).ConfigureAwait(false);
        Assert.AreEqual("fixture-silo-volume-mismatch", copiedBucket.RootElement.GetProperty("reason").GetString());
        using var dryRun = await RunFixtureCommandAsync(arguments, configuration, token).ConfigureAwait(false);
        var sha = dryRun.RootElement.GetProperty("selectionSha256").GetString()!;
        Assert.AreEqual("would-import", dryRun.RootElement.GetProperty("records")[0].GetProperty("status").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(rawRoot, ".silo")), "Dry run must leave the writer State unopened.");
        var command = new[] { SiloFixtureImportCommand.Argument, "publish", source, selectionFile, dataRoot, sha };
        using var publication = await RunFixtureCommandAsync(command, configuration, token).ConfigureAwait(false);
        var imported = publication.RootElement.GetProperty("records")[0];
        Assert.AreEqual("imported", imported.GetProperty("status").GetString());
        using var retry = await RunFixtureCommandAsync(command, configuration, token).ConfigureAwait(false);
        Assert.AreEqual("already-imported", retry.RootElement.GetProperty("records")[0].GetProperty("reason").GetString());

        var alternateSelection = SiloJson.Serialize(new SiloFixtureSelection(SiloFixtureInventory.SelectionFormat,
            "unrelated-export", 1024 * 1024, 1024 * 1024, [new("frame", SiloHash.Sha256(image), SiloHash.Sha256(sidecar))]));
        await File.WriteAllBytesAsync(Path.Combine(source, "other.selection.json"), alternateSelection, token).ConfigureAwait(false);
        var leftover = Path.Combine(rawRoot, ".silo", SiloStateRoot.SpoolDirectoryName, "unclaimed.tmp");
        await File.WriteAllTextAsync(leftover, "still present after refusal", token).ConfigureAwait(false);
        var before = SnapshotFixtureState(rawRoot);
        using var unrelated = await RunFixtureCommandAsync(
            [SiloFixtureImportCommand.Argument, "publish", source, "other.selection.json", dataRoot, SiloHash.Sha256(alternateSelection)],
            configuration, token, expectedExitCode: 2).ConfigureAwait(false);
        Assert.AreEqual("fixture-target-has-other-history", unrelated.RootElement.GetProperty("reason").GetString());
        CollectionAssert.AreEqual(before, SnapshotFixtureState(rawRoot));

        var options = configuration.GetSection("CameraAgent").Get<CameraAgentHostOptions>()!;
        using var storage = new SiloInstalledStorage(SiloInstalledSettings.Read(configuration.GetSection("CameraAgent:Silo")),
            Options.Create(options), TimeProvider.System);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        var reopened = await storage.Catalog.ReopenAsync(imported.GetProperty("groupId").GetString()!, "1", token).ConfigureAwait(false);
        Assert.IsTrue(reopened.Reopenable);
        using var manifest = JsonDocument.Parse(reopened.ManifestJson!);
        var provenance = manifest.RootElement.GetProperty("provenance").GetProperty("importSource");
        Assert.AreEqual("source-capture", provenance.GetProperty("sourceCaptureId").GetString());
        Assert.AreEqual(captured, provenance.GetProperty("capturedToUtc").GetDateTimeOffset());

        var artifactId = imported.GetProperty("artifactId").GetGuid();
        var artifacts = new SiloArtifactStorage(storage, TimeProvider.System);
        CollectionAssert.AreEqual(image, await artifacts.ReadMemberAsync(artifactId, SiloFixtureInventory.ImageRevision,
            image.LongLength, SiloHash.Sha256(image), token).ConfigureAwait(false));
        CollectionAssert.AreEqual(sidecar, await artifacts.ReadMemberAsync(artifactId, SiloFixtureInventory.SidecarRevision,
            sidecar.LongLength, SiloHash.Sha256(sidecar), token).ConfigureAwait(false));
        Assert.AreEqual(2L, (await storage.State.ReadHeadAsync(token).ConfigureAwait(false)).Sequence);
        CollectionAssert.AreEqual(image, await File.ReadAllBytesAsync(Path.Combine(source, "frame.png"), token).ConfigureAwait(false));
        CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(Path.Combine(source, "frame.json"), token).ConfigureAwait(false));
        CollectionAssert.AreEqual(selection, await File.ReadAllBytesAsync(Path.Combine(source, selectionFile), token).ConfigureAwait(false));
    }

    private static string[] SnapshotFixtureState(string stateRoot) =>
        Directory.EnumerateFileSystemEntries(stateRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(static path => File.Exists(path)
                ? $"{path}|{File.GetLastWriteTimeUtc(path):O}|{SiloHash.Sha256(File.ReadAllBytes(path))}"
                : path)
            .ToArray();

    private static async Task<JsonDocument> RunFixtureCommandAsync(string[] args, IConfiguration configuration, CancellationToken token,
        int expectedExitCode = 0)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = await SiloFixtureImportCommand.RunAsync(args, () => configuration, output, token).ConfigureAwait(false);
        Assert.AreEqual(expectedExitCode, exitCode, output.ToString());
        return JsonDocument.Parse(output.ToString());
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint EffectiveGroupId();
}
