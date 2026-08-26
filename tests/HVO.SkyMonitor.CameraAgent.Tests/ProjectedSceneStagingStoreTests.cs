using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProjectedSceneStagingStoreTests
{
    [TestMethod]
    public async Task StageAsync_RestartPreservesExactGeometryAndIdentity()
    {
        var root = CreateRoot();
        try
        {
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            var sceneId = new string('A', 64);
            var stageKey = new string('1', 64);
            string identity;
            using (var first = CreateStore(root))
            {
                await first.StageAsync(stageKey, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
                var staged = await first.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(staged);
                identity = staged.StageIdentitySha256;
                CollectionAssert.AreEqual(scene.Objects.Select(static item => item.Pixel).ToArray(),
                    staged.Objects.Select(static item => item.Pixel).ToArray());
            }

            using var restarted = CreateStore(root);
            var recovered = await restarted.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(identity, recovered.StageIdentitySha256);
            Assert.AreEqual(scene.Objects[0].Pixel, recovered.Objects[0].Pixel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_ExactDuplicateIsIdempotentAndConflictIsRejected()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumFileCount: 1);
            var sceneId = new string('B', 64);
            var stageKey = new string('2', 64);
            var original = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            await store.StageAsync(stageKey, sceneId, original, CancellationToken.None).ConfigureAwait(false);
            await store.StageAsync(stageKey, sceneId, original, CancellationToken.None).ConfigureAwait(false);

            var changed = await CreateSceneAsync(FixtureUtc.AddSeconds(1), 10).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await store.StageAsync(stageKey, sceneId, changed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesOnlyExplicitlyCommittedStage()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var firstId = new string('C', 64);
            var secondId = new string('D', 64);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            var sceneId = new string('A', 64);
            await store.StageAsync(firstId, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            await store.StageAsync(secondId, sceneId, scene, CancellationToken.None).ConfigureAwait(false);

            await store.DeleteAsync(firstId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(await store.ReadAsync(firstId, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNotNull(await store.ReadAsync(secondId, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CompletedMarker_IsIdempotentAndReconciliationDeletesItDespiteRawOwnership()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var stageKey = new string('6', 64);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            await store.StageAsync(stageKey, new string('7', 64), scene, CancellationToken.None).ConfigureAwait(false);

            await store.MarkCompletedAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
            await store.MarkCompletedAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
            var completedPath = Path.Combine(root, "staging", "projected-scenes", $"{stageKey}.completed.json");
            Assert.IsTrue(File.Exists(completedPath));

            await store.ReconcileAsync(new HashSet<string>([stageKey], StringComparer.Ordinal), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsFalse(File.Exists(completedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [TestMethod]
    public async Task StageAsync_EnforcesFileCountBound()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumFileCount: 1);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            await store.StageAsync(new string('E', 64), new string('A', 64), scene, CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await store.StageAsync(new string('F', 64), new string('A', 64), scene, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_RejectsOversizedFileBeforePayloadRead()
    {
        var root = CreateRoot();
        try
        {
            var stageKey = new string('7', 64);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{stageKey}.json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(ProjectedSceneJson.MaximumPayloadBytes + 1L);
            }
            using var store = CreateStore(root);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await store.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAndDeleteAsync_DisappearedStageIsUnavailableAndIdempotent()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var stageKey = new string('8', 64);

            Assert.IsNull(await store.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false));
            await store.DeleteAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_RetainsOwnedAndPrunesCanonicalUnownedAndTemporaryStages()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            var owned = new string('9', 64);
            var unowned = new string('A', 64);
            var sceneId = new string('B', 64);
            await store.StageAsync(owned, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            await store.StageAsync(unowned, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            await File.WriteAllBytesAsync(Path.Combine(directory, ".orphan.tmp"), [1]).ConfigureAwait(false);

            await store.ReconcileAsync(new HashSet<string>([owned], StringComparer.Ordinal), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsNotNull(await store.ReadAsync(owned, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await store.ReadAsync(unowned, CancellationToken.None).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(Path.Combine(directory, ".orphan.tmp")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_OverBoundDeletesInvalidAndAdvancesCursor()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumReconciliationEntries: 1);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            var invalid = Path.Combine(directory, $"{new string('1', 64)}.json");
            await File.WriteAllTextAsync(invalid, "not-json").ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(directory, ".second.tmp"), [1]).ConfigureAwait(false);

            var first = await store.ReconcileAsync(
                new HashSet<string>(StringComparer.Ordinal), CancellationToken.None).ConfigureAwait(false);
            var second = await store.ReconcileAsync(
                new HashSet<string>(StringComparer.Ordinal), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(invalid));
            Assert.IsFalse(File.Exists(Path.Combine(directory, ".second.tmp")));
            Assert.IsTrue(first.BacklogCount > 0);
            Assert.AreEqual(0, second.BacklogCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_InvalidPrefixDoesNotBlockValidOrphanCleanup()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumReconciliationEntries: 4);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            var validKey = new string('F', 64);
            await store.StageAsync(validKey, new string('E', 64), scene, CancellationToken.None).ConfigureAwait(false);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            var invalid = Path.Combine(directory, $"{new string('0', 64)}.json");
            await File.WriteAllTextAsync(invalid, "invalid").ConfigureAwait(false);

            await store.ReconcileAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None)
                .ConfigureAwait(false);
            await store.ReconcileAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsFalse(File.Exists(invalid));
            Assert.IsNull(await store.ReadAsync(validKey, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_ManyOwnedEntriesCompleteCycleWithoutPerpetualBacklog()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumReconciliationEntries: 2);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            var owned = Enumerable.Range(0, 5)
                .Select(index => index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in owned)
                await store.StageAsync(key, new string('A', 64), scene, CancellationToken.None).ConfigureAwait(false);

            ProjectedSceneStageReconciliationResult result;
            var passes = 0;
            do
            {
                result = await store.ReconcileAsync(owned, CancellationToken.None).ConfigureAwait(false);
                passes++;
            } while (result.BacklogCount > 0 && passes < 10);

            Assert.AreEqual(0, result.BacklogCount);
            Assert.AreEqual(3, passes);
            Assert.AreEqual(5, Directory.EnumerateFiles(Path.Combine(root, "staging", "projected-scenes"), "*.json").Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_ManyInvalidEntriesAreDeletedWithoutPerpetualBacklog()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumReconciliationEntries: 2);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            for (var index = 0; index < 5; index++)
                await File.WriteAllTextAsync(Path.Combine(directory,
                    $"{index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture)}.json"), "invalid")
                    .ConfigureAwait(false);

            ProjectedSceneStageReconciliationResult result;
            do
            {
                result = await store.ReconcileAsync(new HashSet<string>(), CancellationToken.None).ConfigureAwait(false);
            } while (result.BacklogCount > 0);

            Assert.AreEqual(0, result.BacklogCount);
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.json").Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_InvalidKeyReleasesHardCapacity()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumFileCount: 1);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            var invalid = Path.Combine(directory, "invalid.json");
            await File.WriteAllTextAsync(invalid, "invalid").ConfigureAwait(false);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await store.StageAsync(new string('A', 64), new string('B', 64), scene, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            await store.ReconcileAsync(new HashSet<string>(), CancellationToken.None).ConfigureAwait(false);
            await store.StageAsync(new string('A', 64), new string('B', 64), scene, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsFalse(File.Exists(invalid));
            Assert.IsNotNull(await store.ReadAsync(new string('A', 64), CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_OwnedInvalidEvidenceIsRetainedUntouched()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var stageKey = new string('D', 64);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{stageKey}.json");
            byte[] bytes = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

            await store.ReconcileAsync(
                new HashSet<string>([stageKey], StringComparer.Ordinal), CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_TransientInspectionFailureRemainsVisibleAndDoesNotDeleteEvidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateRoot();
        var target = string.Concat(root, "-target");
        try
        {
            using var store = CreateStore(root);
            var stageKey = new string('E', 64);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(target, "retained").ConfigureAwait(false);
            var path = Path.Combine(directory, $"{stageKey}.json");
            File.CreateSymbolicLink(path, target);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
                await store.ReconcileAsync(new HashSet<string>(), CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual("retained", await File.ReadAllTextAsync(target).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(target);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_ActionablesDrainAcrossBatchesAndNewTempAfterZeroIsDiscovered()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root, maximumReconciliationEntries: 2);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            for (var index = 0; index < 5; index++)
                await File.WriteAllBytesAsync(Path.Combine(directory, $".{index}.tmp"), [1]).ConfigureAwait(false);

            ProjectedSceneStageReconciliationResult result;
            do
            {
                result = await store.ReconcileAsync(new HashSet<string>(), CancellationToken.None).ConfigureAwait(false);
            } while (result.BacklogCount > 0);
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.tmp").Count());

            var later = Path.Combine(directory, ".later.tmp");
            await File.WriteAllBytesAsync(later, [1]).ConfigureAwait(false);
            var periodic = await store.ReconcileAsync(new HashSet<string>(), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(later));
            Assert.AreEqual(0, periodic.BacklogCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_LinuxRejectsStageSymlinkWithoutFollowingTarget()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var directory = Path.Combine(root, "staging", "projected-scenes");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(root, "target.json");
            await File.WriteAllTextAsync(target, "secret").ConfigureAwait(false);
            var stageKey = new string('2', 64);
            File.CreateSymbolicLink(Path.Combine(directory, $"{stageKey}.json"), target);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
                await store.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_ConcurrentStoreInstancesAuthenticateIdenticalAndRejectConflict()
    {
        var root = CreateRoot();
        try
        {
            using var first = CreateStore(root);
            using var second = CreateStore(root);
            var stageKey = new string('3', 64);
            var sceneId = new string('4', 64);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);

            await Task.WhenAll(
                first.StageAsync(stageKey, sceneId, scene, CancellationToken.None).AsTask(),
                second.StageAsync(stageKey, sceneId, scene, CancellationToken.None).AsTask()).ConfigureAwait(false);
            var changed = await CreateSceneAsync(FixtureUtc.AddSeconds(1), 10).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await second.StageAsync(stageKey, sceneId, changed, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EnsureAncestorChainHasNoLinks_RejectsConfiguredRootAncestorSymlink()
    {
        var root = CreateRoot();
        try
        {
            var physical = Path.Combine(root, "physical");
            Directory.CreateDirectory(physical);
            var link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, physical);

            Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                ProjectedSceneStagingStore.EnsureAncestorChainHasNoLinks(link));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectedSceneStagingStore CreateStore(
        string root,
        int maximumFileCount = 128,
        int maximumReconciliationEntries = 512) => new(
        Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            ProjectedSceneStaging = new ProjectedSceneStagingOptions
            {
                MaximumFileCount = maximumFileCount,
                MaximumTotalBytes = 128L * 1024 * 1024,
                MaximumReconciliationEntries = maximumReconciliationEntries
            }
        }));

    private static async ValueTask<VisibleScene> CreateSceneAsync(DateTimeOffset utc, double rightAscensionHours)
    {
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("test-star", "Test Star", rightAscensionHours, 45, 1)
        ]);
        return await new VisibleSceneBuilder(catalog).BuildAsync(new VisibleSceneRequest(
            utc, new ObserverLocation(35, -115, 1000),
            new EquidistantProjectionContext(32, 32, 25, 25, WidthPixels: 64, HeightPixels: 64),
            new CatalogQuery(6.5, 10),
            new CatalogMetadata("test", "1", new Uri("https://example.invalid/catalog"), new string('0', 64), "test", "1"),
            projectionVersion: "test-projection-v1",
            algorithmVersion: "test-astronomy-v1")).ConfigureAwait(false);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static DateTimeOffset FixtureUtc { get; } = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
}
