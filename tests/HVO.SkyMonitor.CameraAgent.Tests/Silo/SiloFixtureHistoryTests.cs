using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloFixtureHistoryTests
{
    private const string SelectionFile = "test.selection.json";
    private const long Reserve = 10 * 1024 * 1024;
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNoAAAAggCBd81ytgAAAABJRU5ErkJggg==");
    private static CancellationToken Token => CancellationToken.None;

    [TestMethod]
    public async Task InventoryAndDryRunReportExactRolesDatesBudgetAndUsableLineageWithoutWrites()
    {
        using var fixture = new Fixture();
        var old = fixture.WritePair("old", "art-1", daysOld: 45);
        var dependency = new SiloFixtureSourceDependency("art-1", old.ImageSha256);
        var recent = fixture.WritePair("recent", "art-2", daysOld: 1, dependency);
        var unsupported = fixture.WritePair("unsupported", "art-3", daysOld: 2, format: "unknown");
        var missing = fixture.WritePair("missing", "art-4", daysOld: 2, new("absent", old.ImageSha256));
        fixture.Select([old, recent, unsupported, missing]);
        var before = fixture.Snapshot();

        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        var import = new SiloFixtureImport(source, inventory);
        var report = await import.RunAsync(SiloFixtureMode.DryRun, () => new(Reserve * 2, Reserve * 2), null, Token).ConfigureAwait(false);

        report.Mode.Should().Be(SiloFixtureMode.DryRun);
        report.SourceId.Should().Be("sanitized-export");
        report.CapturedFromUtc.Should().Be(Now.AddDays(-45).AddSeconds(-1));
        report.CapturedToUtc.Should().Be(Now.AddDays(-1));
        report.SelectionSha256.Should().Be(before[SelectionFile].Sha);
        report.Records.Select(static row => row.Status).Should().Equal("would-import", "would-import", "skipped", "skipped");
        report.Records[0].ImmediatelyAgeEligible.Should().BeTrue();
        report.Records[1].ImmediatelyAgeEligible.Should().BeFalse();
        report.Records[1].Objects!.Select(static item => item.Role).Should().Equal("fixture-image", "fixture-sidecar");
        report.Records[1].Source!.SourceCaptureId.Should().Be("capture-recent");
        report.Records[1].UsableReopenImage.Should().Be(dependency);
        report.Records[2].Reason.Should().Be("unsupported-sidecar");
        report.Records[3].Reason.Should().Be("incomplete-reopen-lineage");
        report.PlannedBytes.Should().Be(before["old.png"].Length + before["old.json"].Length +
            before["recent.png"].Length + before["recent.json"].Length);
        fixture.Snapshot().Should().BeEquivalentTo(before);
    }

    [TestMethod]
    public async Task CommandWritesStructuredInventoryWithoutLoadingTargetAndRejectsChangedSelectionBeforeOpeningTarget()
    {
        using var fixture = new Fixture();
        fixture.Select([fixture.WritePair("frame", "art-1")]);
        var before = fixture.Snapshot();
        IConfiguration RefuseTarget() => throw new InvalidOperationException("Target configuration must not be loaded.");
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var exit = await SiloFixtureImportCommand.RunAsync(
            [SiloFixtureImportCommand.Argument, "inventory", fixture.Root, SelectionFile], RefuseTarget, output, Token).ConfigureAwait(false);

        exit.Should().Be(0);
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("format").GetString().Should().Be(SiloFixtureImport.ReportFormat);
        json.RootElement.GetProperty("selectionSha256").GetString().Should().Be(before[SelectionFile].Sha);
        json.RootElement.GetProperty("records")[0].GetProperty("status").GetString().Should().Be("selected");
        output.GetStringBuilder().Clear();
        var changedExit = await SiloFixtureImportCommand.RunAsync(
            [SiloFixtureImportCommand.Argument, "publish", fixture.Root, SelectionFile, fixture.Root, new string('0', 64)],
            RefuseTarget, output, Token).ConfigureAwait(false);
        changedExit.Should().Be(2);
        using var refusal = JsonDocument.Parse(output.ToString());
        refusal.RootElement.GetProperty("reason").GetString().Should().Be("fixture-selection-changed");
        fixture.Snapshot().Should().BeEquivalentTo(before);
    }

    [TestMethod]
    public async Task DuplicateSelectionAndSourceIdsFailEveryClaimAndTraversalIsRefused()
    {
        using var fixture = new Fixture();
        var first = fixture.WritePair("first", "same");
        var second = fixture.WritePair("second", "same");
        var third = fixture.WritePair("third", "different");
        fixture.Select([first, second, third, third with { Stem = "THIRD" }, first with { Stem = "../outside" }]);
        using var source = fixture.Open();
        var report = await InventoryAsync(source).ConfigureAwait(false);
        report.Records.Select(static row => row.Reason).Should().Equal("duplicate-source-artifact", "duplicate-source-artifact",
            "duplicate-selection", "duplicate-selection", "invalid-selection-entry");
        report.Records.Should().OnlyContain(static row => row.Status == "failed");
    }

    [TestMethod]
    public async Task SourceRootAncestorsAndSelectedSymlinksAreNeverFollowed()
    {
        using var fixture = new Fixture();
        using var outside = new Fixture();
        var entry = outside.WritePair("outside", "art");
        File.Copy(outside.PathFor("outside.json"), fixture.PathFor("outside.json"));
        File.CreateSymbolicLink(fixture.PathFor("outside.png"), outside.PathFor("outside.png"));
        fixture.Select([entry]);
        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);

        inventory.Records.Single().Reason.Should().Be("source-unreadable-or-nonconfined");
        Directory.CreateSymbolicLink(fixture.PathFor("folder-link"), outside.Root);
        var openLink = () => new ConfinedReadDirectory(fixture.PathFor("folder-link"));
        var openChild = () => new ConfinedReadDirectory(fixture.PathFor("folder-link/child"));
        openLink.Should().Throw<IOException>();
        openChild.Should().Throw<IOException>();
    }

    [TestMethod]
    public async Task HardLinkedImagesAndSymlinkedSelectionAreRefused()
    {
        using var fixture = new Fixture();
        var original = fixture.WritePair("frame", "art");
        fixture.Select([original]);
        var link = new System.Diagnostics.ProcessStartInfo("/bin/ln") { UseShellExecute = false };
        link.ArgumentList.Add(fixture.PathFor("frame.png"));
        link.ArgumentList.Add(fixture.PathFor("another-name.png"));
        using var process = System.Diagnostics.Process.Start(link)!;
        await process.WaitForExitAsync(Token).ConfigureAwait(false);
        process.ExitCode.Should().Be(0);

        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        inventory.Records.Single().Reason.Should().Be("source-unreadable-or-nonconfined");
        File.Move(fixture.PathFor(SelectionFile), fixture.PathFor("actual-selection.json"));
        File.CreateSymbolicLink(fixture.PathFor(SelectionFile), fixture.PathFor("actual-selection.json"));
        var retry = () => InventoryAsync(source).AsTask();
        await retry.Should().ThrowAsync<IOException>().ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("frame.png", false)]
    [DataRow("frame.json", false)]
    [DataRow("frame.png", true)]
    public async Task ChangedOrReplacedFilesBetweenInventoryAndPublicationNeverReachStaging(string file, bool useLink)
    {
        using var fixture = new Fixture();
        using var outside = new Fixture();
        fixture.Select([fixture.WritePair("frame", "art-1")]);
        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        var path = fixture.PathFor(file);
        var bytes = await File.ReadAllBytesAsync(path, Token).ConfigureAwait(false);
        if (useLink)
        {
            await File.WriteAllBytesAsync(outside.PathFor(file), bytes, Token).ConfigureAwait(false);
            File.Delete(path);
            File.CreateSymbolicLink(path, outside.PathFor(file));
        }
        else
        {
            bytes[^1] ^= 1;
            await File.WriteAllBytesAsync(path, bytes, Token).ConfigureAwait(false);
        }

        var report = await new SiloFixtureImport(source, inventory).RunAsync(SiloFixtureMode.Publish,
            () => new(Reserve * 2, Reserve * 2), static (_, _, _) => throw new InvalidOperationException("Unexpected staging."), Token)
            .ConfigureAwait(false);
        report.Records.Single().Status.Should().Be("failed");
        report.Records.Single().Reason.Should().Be("source-changed-or-nonconfined");
        (await File.ReadAllBytesAsync(path, Token).ConfigureAwait(false)).Should().Equal(bytes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BudgetStopsBeforeTheNextPairAndLeavesNoPartialPublication(bool useByteLimit)
    {
        using var fixture = new Fixture();
        var first = fixture.WritePair("first", "art-1");
        var second = fixture.WritePair("second", "art-2");
        var firstLength = new FileInfo(fixture.PathFor("first.png")).Length + new FileInfo(fixture.PathFor("first.json")).Length;
        fixture.Select([first, second], maximumBytes: useByteLimit ? firstLength : 1024 * 1024);
        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        var publications = 0;
        var report = await new SiloFixtureImport(source, inventory).RunAsync(SiloFixtureMode.Publish,
            () => new(Reserve * 2, !useByteLimit && publications > 0 ? Reserve : Reserve * 2), (_, _, _) =>
            {
                publications++;
                return ValueTask.FromResult(true);
            }, Token).ConfigureAwait(false);

        publications.Should().Be(1);
        report.PlannedBytes.Should().Be(firstLength);
        report.Records[0].Status.Should().Be("imported");
        report.Records[1].Reason.Should().Be(useByteLimit ? "byte-limit" : "free-space-reserve");
    }

    [TestMethod]
    [DataRow(nameof(SiloFaultPoint.AfterSegmentCommitted))]
    [DataRow(nameof(SiloFaultPoint.AfterVisibleCommitted))]
    public async Task InterruptedPublicationRetriesOnceAndReadsAndReplaysBothExactObjects(string fault)
    {
        using var fixture = new Fixture();
        var first = fixture.WritePair("first", "art-1", daysOld: 2);
        var next = fixture.WritePair("next", "art-2", daysOld: 1, new("art-1", first.ImageSha256));
        fixture.Select([first, next]);
        var before = fixture.Snapshot();
        using var source = fixture.Open();
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        if (DurableSync.SupportsDirectorySync)
        {
            silo.FileSpoolCapacity = (1024 * 1024, 8);
            await silo.RestartAsync().ConfigureAwait(false);
        }
        silo.Faults.Armed = Enum.Parse<SiloFaultPoint>(fault);
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        var import = new SiloFixtureImport(source, inventory);
        var interrupted = () => PublishAsync(import, silo);
        await interrupted.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        fixture.Snapshot().Should().BeEquivalentTo(before);

        await silo.RestartAsync().ConfigureAwait(false);
        var replay = await PublishAsync(import, silo).ConfigureAwait(false);
        replay.Records.Select(static row => row.Status).Should().Equal(
            fault == nameof(SiloFaultPoint.AfterVisibleCommitted) ? "skipped" : "imported", "imported");
        var rerun = await PublishAsync(import, silo).ConfigureAwait(false);
        rerun.Records.Select(static row => row.Reason).Should().Equal("already-imported", "already-imported");
        (await silo.State.ListUnreleasedAsync(8, Token).ConfigureAwait(false)).Should().BeEmpty();
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().HaveCount(2).And.OnlyContain(static row => row.Capture == null);
        foreach (var record in replay.Records)
        {
            var published = silo.PublishRecord(record.GroupId!, "1");
            published.GroupKind.Should().Be(SiloFixtureInventory.GroupKind);
            foreach (var member in published.Members)
            {
                var item = record.Objects!.Single(item => item.Sha256 == member.Sha256);
                var expected = await File.ReadAllBytesAsync(fixture.PathFor(item.File), Token).ConfigureAwait(false);
                silo.Local.Read(member.Locator.Key, member.Locator.VersionId).Should().Equal(expected);
                member.Length.Should().Be(item.Length);
            }
        }

        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        var visible = await silo.Index.QueryVisibleAsync(Now.AddDays(-3), Now, "camera", 10, Token).ConfigureAwait(false);
        visible.Select(static row => row.GroupId).Should().Equal(replay.Records.Select(static row => row.GroupId));
        var group = await silo.Catalog.ReopenAsync(replay.Records[1].GroupId!, "1", Token).ConfigureAwait(false);
        group.Reopenable.Should().BeTrue();
        var manifest = SiloJson.ReadManifest(System.Text.Encoding.UTF8.GetBytes(group.ManifestJson!), "fixture");
        manifest.Provenance.ImportSource!.Value.GetProperty("sourceArtifactId").GetString().Should().Be("art-2");
        manifest.Provenance.ImportSource!.Value.GetProperty("capturedToUtc").GetDateTimeOffset().Should().Be(Now.AddDays(-1));
        manifest.Dependencies.Single().Sha256.Should().Be(first.ImageSha256);
        fixture.Snapshot().Should().BeEquivalentTo(before);
    }

    [TestMethod]
    public async Task UpdatedSelectionCannotReplaceTheSameSourceArtifactWithDifferentFacts()
    {
        using var fixture = new Fixture();
        fixture.Select([fixture.WritePair("first", "art-1", daysOld: 1)]);
        using var source = fixture.Open();
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var original = await InventoryAsync(source).ConfigureAwait(false);
        await PublishAsync(new(source, original), silo).ConfigureAwait(false);
        fixture.Select([fixture.WritePair("first", "art-1", daysOld: 2)]);
        var changed = await InventoryAsync(source).ConfigureAwait(false);
        changed.Records[0].GroupId.Should().Be(original.Records[0].GroupId);
        var conflict = await PublishAsync(new(source, changed), silo).ConfigureAwait(false);
        conflict.Records.Single().Reason.Should().Be("silo.revision-conflict");
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().ContainSingle();
    }

    private static ValueTask<SiloFixtureInventory> InventoryAsync(ConfinedReadDirectory source) =>
        SiloFixtureInventory.ReadAsync(source, SelectionFile, Now, Token);

    private static Task<SiloFixtureReport> PublishAsync(SiloFixtureImport import, SiloHarness silo) =>
        import.RunAsync(SiloFixtureMode.Publish, () => new(Reserve * 2, Reserve * 2), (draft, open, token) =>
            SiloFixtureImport.PublishPairAsync(silo.State, silo.Publisher, silo.Catalog, draft, open, token), Token).AsTask();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("silo-history-fixture");

        public string PathFor(string leaf) => Path.Combine(Root, leaf);

        public ConfinedReadDirectory Open() => new(Root);

        public SiloFixtureSelectionEntry WritePair(string stem, string artifactId, int daysOld = 1,
            SiloFixtureSourceDependency? dependency = null, string format = SiloFixtureInventory.SidecarFormat)
        {
            var end = Now.AddDays(-daysOld);
            var sidecar = SiloJson.Serialize(new SiloFixtureSidecar(format, "capture-" + stem, "camera", artifactId,
                end.AddSeconds(-1), end, dependency));
            File.WriteAllBytes(PathFor(stem + ".png"), Png);
            File.WriteAllBytes(PathFor(stem + ".json"), sidecar);
            return new(stem, SiloHash.Sha256(Png), SiloHash.Sha256(sidecar));
        }

        public void Select(SiloFixtureSelectionEntry[] entries, long maximumBytes = 1024 * 1024) =>
            File.WriteAllBytes(PathFor(SelectionFile), SiloJson.Serialize(
                new SiloFixtureSelection(SiloFixtureInventory.SelectionFormat, "sanitized-export", maximumBytes, Reserve, 30, entries)));

        public Dictionary<string, (string Sha, long Length, DateTime Changed)> Snapshot() =>
            Directory.EnumerateFiles(Root).ToDictionary(static path => Path.GetFileName(path), static path =>
            {
                var bytes = File.ReadAllBytes(path);
                return (SiloHash.Sha256(bytes), bytes.LongLength, File.GetLastWriteTimeUtc(path));
            }, StringComparer.Ordinal);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
