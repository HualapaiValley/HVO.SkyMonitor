using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = "The fixture importer is qualified against Linux State and Silo volumes.")]
public sealed class SiloFixtureHistoryTests
{
    internal const string SelectionFile = "test.selection.json";
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
        using var output = new StringWriter(CultureInfo.InvariantCulture);
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
    [DataRow("resumed")]
    [DataRow("unconfigured")]
    [DataRow("raw-ingress.db")]
    [DataRow("raw-ingress.db-wal")]
    public async Task PublishRefusesTargetsWithoutAnIntactFirstBootPause(string condition)
    {
        using var fixture = new Fixture();
        var digest = fixture.Select([fixture.WritePair("frame", "art-1")]);
        using var target = new Fixture();
        var stateRoot = Directory.CreateDirectory(target.PathFor("state")).FullName;
        var storeRoot = Directory.CreateDirectory(target.PathFor("store")).FullName;
        var seed = new FirstBootPauseIdentity(Guid.NewGuid());
        var settings = TargetSettings(stateRoot, out var bucket, condition == "unconfigured" ? null : seed.Id);
        Directory.CreateDirectory(Path.Combine(storeRoot, bucket));
        await FirstBootStateInitializer.InitializeAsync(stateRoot, 1, seed, Token).ConfigureAwait(false);

        if (condition == "resumed")
        {
            var journal = Path.Combine(stateRoot, "journal", "raw-ingress.db");
            var control = new SqliteCaptureControlStore(journal, 1, TimeProvider.System);
            var resume = await control.BeginAsync(CaptureControlTarget.Running, seed.ResumeKey, 1, seed.Actor,
                FirstBootPauseIdentity.ResumeReason, Token, firstBootPause: seed).ConfigureAwait(false);
            resume.Snapshot.Version.Should().Be(2);
            resume.Snapshot.State.Should().Be(CaptureAdmissionState.Running);
        }
        else if (condition != "unconfigured")
        {
            var path = Path.Combine(stateRoot, "journal", condition);
            var referent = target.PathFor(condition);
            if (File.Exists(path))
                File.Move(path, referent);
            else
                await File.WriteAllTextAsync(referent, "must not be read or changed", Token).ConfigureAwait(false);
            File.CreateSymbolicLink(path, referent);
        }

        var before = SnapshotTree(target.Root);
        var source = fixture.Snapshot();
        await AssertCommandRefusalAsync(
            [SiloFixtureImportCommand.Argument, "publish", fixture.Root, SelectionFile, storeRoot, digest],
            () => new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            "fixture-first-boot-pause-required").ConfigureAwait(false);

        Directory.Exists(Path.Combine(stateRoot, ".silo")).Should().BeFalse();
        SnapshotTree(target.Root).Should().BeEquivalentTo(before);
        fixture.Snapshot().Should().BeEquivalentTo(source);
    }

    [TestMethod]
    [DataRow("source/state", "fixture-overlapping-roots")]
    [DataRow("source/store", "fixture-overlapping-roots")]
    [DataRow("state/store", "fixture-overlapping-roots")]
    [DataRow("missing-bucket", "fixture-silo-volume-mismatch")]
    public async Task DryRunRefusesOverlappingRootsOrAMismatchedStoreWithoutWriting(string condition, string reason)
    {
        using var fixture = new Fixture();
        fixture.Select([fixture.WritePair("frame", "art-1")]);
        using var target = new Fixture();
        var stateRoot = Directory.CreateDirectory(Path.Combine(condition == "source/state" ? fixture.Root : target.Root, "state")).FullName;
        var storeParent = condition switch
        {
            "source/store" => fixture.Root,
            "state/store" => stateRoot,
            _ => target.Root
        };
        var storeRoot = Directory.CreateDirectory(Path.Combine(storeParent, "store")).FullName;
        var settings = TargetSettings(stateRoot, out var bucket);
        if (condition != "missing-bucket")
            Directory.CreateDirectory(Path.Combine(storeRoot, bucket));
        var beforeTarget = SnapshotTree(target.Root);
        var beforeSource = SnapshotTree(fixture.Root);

        await AssertCommandRefusalAsync(
            [SiloFixtureImportCommand.Argument, "dry-run", fixture.Root, SelectionFile, storeRoot],
            () => new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), reason).ConfigureAwait(false);

        SnapshotTree(target.Root).Should().BeEquivalentTo(beforeTarget);
        SnapshotTree(fixture.Root).Should().BeEquivalentTo(beforeSource);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "non-fixture group using the selected identity and revision")]
    [DataRow(true, DisplayName = "fixture group from a different selection")]
    public async Task TargetHistoryGuardRefusesExistingUnselectedHistoryWithoutWriting(bool otherFixture)
    {
        using var fixture = new Fixture();
        var requested = fixture.WritePair("frame", "art-1");
        fixture.Select([requested]);
        using var source = fixture.Open();
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var requestedGroup = (await InventoryAsync(source).ConfigureAwait(false)).Records.Single().GroupId!;
        if (otherFixture)
        {
            fixture.Select([fixture.WritePair("other", "art-2")]);
            var other = await InventoryAsync(source).ConfigureAwait(false);
            (await PublishAsync(new(source, other), silo).ConfigureAwait(false)).Records.Single().Status.Should().Be("imported");
            fixture.Select([requested]);
        }
        else
        {
            var draft = SiloDrafts.Evidence(silo.Payloads, "non-fixture") with { GroupId = requestedGroup, RevisionId = "1" };
            (await silo.PublishAsync(draft).ConfigureAwait(false)).State.Should().Be(SiloStagedState.Visible);
        }

        var beforeState = SnapshotTree(silo.RootPath);
        var beforeStore = silo.Local.Fingerprint();
        using var scratch = new Fixture();
        var requireTarget = () => silo.State.InspectFixtureTargetAsync(scratch.Root,
            new HashSet<string>(StringComparer.Ordinal) { requestedGroup }, Token).AsTask();

        var refusal = await requireTarget.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false);

        refusal.Which.Code.Should().Be("fixture-target-has-other-history");
        silo.Local.Fingerprint().Should().Be(beforeStore);
        SnapshotTree(silo.RootPath).Should().BeEquivalentTo(beforeState);
    }

    [TestMethod]
    public async Task PublishRefusesADifferentPendingGroupWithoutChangingTheJournalOrStore()
    {
        using var fixture = new Fixture();
        var pendingEntry = fixture.WritePair("pending", "art-1");
        var nextEntry = fixture.WritePair("next", "art-2");
        fixture.Select([pendingEntry]);
        using var source = fixture.Open();
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var original = await InventoryAsync(source).ConfigureAwait(false);
        silo.Faults.Armed = SiloFaultPoint.AfterSegmentPending;
        var start = () => PublishAsync(new(source, original), silo);
        await start.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        var pending = await silo.State.ReadPendingAsync(Token).ConfigureAwait(false);
        pending.Should().NotBeNull();
        pending!.GroupId.Should().Be(original.Records.Single().GroupId);

        fixture.Select([nextEntry, pendingEntry]);
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        using var scratch = new Fixture();
        await silo.State.InspectFixtureTargetAsync(scratch.Root,
            inventory.Records.Select(static row => row.GroupId!).ToHashSet(StringComparer.Ordinal), Token).ConfigureAwait(false);
        var beforeState = SnapshotTree(silo.RootPath);
        var beforeStore = silo.Local.Fingerprint();

        var result = await PublishAsync(new(source, inventory), silo).ConfigureAwait(false);

        result.Records.Select(static row => row.Status).Should().Equal("failed", "skipped");
        result.Records.Select(static row => row.Reason).Should().Equal("fixture-other-publication-pending", "deferred-after-failure");
        (await silo.State.ReadPendingAsync(Token).ConfigureAwait(false)).Should().Be(pending);
        (await silo.State.ReadGroupAsync(result.Records[0].GroupId!, "1", Token).ConfigureAwait(false)).Should().BeNull();
        silo.Local.Fingerprint().Should().Be(beforeStore);
        SnapshotTree(silo.RootPath).Should().BeEquivalentTo(beforeState);
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
    [DataRow("frame.png", 0)]
    [DataRow("frame.png", 8 * 1024 * 1024 + 1)]
    [DataRow("frame.json", 0)]
    [DataRow("frame.json", 16 * 1024 + 1)]
    public async Task InventorySkipsEmptyAndOversizedPairMembers(string file, int length)
    {
        using var fixture = new Fixture();
        fixture.Select([fixture.WritePair("frame", "art-1")]);
        Resize(fixture.PathFor(file), length);

        await AssertInventoryRecordAsync(fixture, "skipped", "unsupported-source-size").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(16 * 1024 + 1)]
    public async Task InventoryRefusesAnEmptyOrOversizedSelectionFile(int length)
    {
        using var fixture = new Fixture();
        fixture.Select([fixture.WritePair("frame", "art-1")]);
        Resize(fixture.PathFor(SelectionFile), length);

        await AssertInventoryRefusalAsync(fixture, "unsupported-source-size").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(0, 1024 * 1024)]
    [DataRow(9, 1024 * 1024)]
    [DataRow(1, 32 * 1024 * 1024 + 1)]
    public async Task InventoryRefusesSelectionCountOrBudgetOutsideTheBounds(int count, int maximumBytes)
    {
        using var fixture = new Fixture();
        var entries = Enumerable.Range(0, count).Select(index => fixture.WritePair($"frame-{index}", $"art-{index}")).ToArray();
        fixture.Select(entries, maximumBytes);

        await AssertInventoryRefusalAsync(fixture, "fixture-invalid-selection").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "image checksum mismatch")]
    [DataRow(false, DisplayName = "sidecar checksum mismatch")]
    public async Task InventoryFailsWhenAnInitialMemberChecksumDiffers(bool image)
    {
        using var fixture = new Fixture();
        var entry = fixture.WritePair("frame", "art-1");
        var wrong = SiloHash.Sha256("different contents"u8);
        fixture.Select([image ? entry with { ImageSha256 = wrong } : entry with { SidecarSha256 = wrong }]);

        await AssertInventoryRecordAsync(fixture, "failed", "source-checksum-mismatch").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("signature")]
    [DataRow("header")]
    [DataRow("trailer")]
    [DataRow("no-image-data")]
    [DataRow("invalid-image-data")]
    public async Task InventorySkipsMalformedOrUndecodablePngs(string defect)
    {
        using var fixture = new Fixture();
        var entry = fixture.WritePair("frame", "art-1");
        var image = Png.ToArray();
        switch (defect)
        {
            case "signature": Array.Fill(image, (byte)'x'); break;
            case "header": image[12] ^= 1; break;
            case "trailer": image[^1] ^= 1; break;
            case "no-image-data": image = [.. image.AsSpan(0, 33), .. image.AsSpan(image.Length - 12)]; break;
            case "invalid-image-data":
                // Replace IDAT with a valid CRC and zlib header followed by a reserved DEFLATE block type.
                Convert.FromHexString("0000000A49444154789C0768000000820081236CDCF7").CopyTo(image, 33);
                using (var data = SKData.CreateCopy(image))
                using (var codec = SKCodec.Create(data))
                {
                    codec.Should().NotBeNull("the PNG headers can still be read");
                    codec!.EncodedFormat.Should().Be(SKEncodedImageFormat.Png);
                }
                break;
        }
        await File.WriteAllBytesAsync(fixture.PathFor("frame.png"), image, Token).ConfigureAwait(false);
        fixture.Select([entry with { ImageSha256 = SiloHash.Sha256(image) }]);

        await AssertInventoryRecordAsync(fixture, "skipped", "unsupported-image").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "malformed sidecar JSON")]
    [DataRow(true, DisplayName = "unmapped sidecar JSON property")]
    public async Task InventorySkipsMalformedOrUnmappedSidecarJson(bool unmappedProperty)
    {
        using var fixture = new Fixture();
        var entry = fixture.WritePair("frame", "art-1");
        var path = fixture.PathFor("frame.json");
        var json = (await File.ReadAllTextAsync(path, Token).ConfigureAwait(false))[..^1];
        var bytes = Encoding.UTF8.GetBytes(unmappedProperty ? json + ",\"unexpected\":true}" : json);
        await File.WriteAllBytesAsync(path, bytes, Token).ConfigureAwait(false);
        fixture.Select([entry with { SidecarSha256 = SiloHash.Sha256(bytes) }]);

        await AssertInventoryRecordAsync(fixture, "skipped", "unsupported-sidecar").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("frame.png")]
    [DataRow("frame.json")]
    public async Task InventoryFailsWhenEitherMemberOfAPairIsMissing(string file)
    {
        using var fixture = new Fixture();
        fixture.Select([fixture.WritePair("frame", "art-1")]);
        File.Delete(fixture.PathFor(file));

        await AssertInventoryRecordAsync(fixture, "failed", "source-unreadable-or-nonconfined").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(1, 2, 0, 0, DisplayName = "future interval")]
    [DataRow(-1, -2, 0, 0, DisplayName = "inverted interval")]
    [DataRow(-2, -1, 1, 0, DisplayName = "non-UTC start")]
    [DataRow(-2, -1, 0, 1, DisplayName = "non-UTC end")]
    public async Task InventorySkipsUnsupportedExposureTimes(int startDay, int endDay, int startOffset, int endOffset)
    {
        using var fixture = new Fixture();
        var facts = new SiloFixtureSidecar(SiloFixtureInventory.SidecarFormat, "capture", "camera", "art-1",
            Now.AddDays(startDay).ToOffset(TimeSpan.FromHours(startOffset)), Now.AddDays(endDay).ToOffset(TimeSpan.FromHours(endOffset)));
        fixture.Select([fixture.WritePair("frame", facts)]);

        await AssertInventoryRecordAsync(fixture, "skipped", "unsupported-sidecar").ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "dependency digest differs from the selected image")]
    [DataRow(false, DisplayName = "dependency ends after the dependent exposure starts")]
    public async Task InventorySkipsDependencyDigestOrTimestampMismatches(bool wrongDigest)
    {
        using var fixture = new Fixture();
        var first = fixture.WritePair("first", "art-1", daysOld: wrongDigest ? 2 : 1);
        var digest = wrongDigest ? SiloHash.Sha256("different dependency"u8) : first.ImageSha256;
        var second = fixture.WritePair("second", "art-2", daysOld: 1, new("art-1", digest));
        fixture.Select([first, second]);
        using var source = fixture.Open();

        var inventory = await InventoryAsync(source).ConfigureAwait(false);

        inventory.Records[0].Status.Should().Be("selected");
        inventory.Records[1].Status.Should().Be("skipped");
        inventory.Records[1].Reason.Should().Be("incomplete-reopen-lineage");
        inventory.Records[1].UsableReopenImage.Should().BeNull();
    }

    [TestMethod]
    public async Task PublicationSkipsAResolvedDependencyThatHasNotSettled()
    {
        using var fixture = new Fixture();
        var first = fixture.WritePair("first", "art-1", daysOld: 2);
        var dependency = new SiloFixtureSourceDependency("art-1", first.ImageSha256);
        var second = fixture.WritePair("second", "art-2", daysOld: 1, dependency);
        fixture.Select([first, second]);
        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        inventory.Records[1].UsableReopenImage.Should().Be(dependency);

        // Pass only the dependent row to exercise the publication guard independently of inventory validation.
        var import = new SiloFixtureImport(source, inventory with { Records = [inventory.Records[1]] });
        var report = await import.RunAsync(SiloFixtureMode.Publish, () => new(Reserve * 2, Reserve * 2),
            static (_, _, _, _) => throw new InvalidOperationException("A dependent row must not be staged."), Token).ConfigureAwait(false);

        report.Records.Single().Status.Should().Be("skipped");
        report.Records.Single().Reason.Should().Be("reopen-dependency-unavailable");
        report.PlannedBytes.Should().Be(0);
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
            () => new(Reserve * 2, Reserve * 2), static (_, _, _, _) => throw new InvalidOperationException("Unexpected staging."), Token)
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
            () => new(Reserve * 2, !useByteLimit && publications > 0 ? Reserve : Reserve * 2), (_, _, requireHeadroom, _) =>
            {
                requireHeadroom();
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
            published.GroupKind.Should().Be("import");
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

    [TestMethod]
    public async Task DifferentPngDigestsProduceDifferentImportedIdentitiesForTheSameSource()
    {
        using var fixture = new Fixture();
        var first = fixture.WritePair("frame", "art-1");
        fixture.Select([first]);
        using var source = fixture.Open();
        var firstInventory = await InventoryAsync(source).ConfigureAwait(false);
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var imported = await PublishAsync(new(source, firstInventory), silo).ConfigureAwait(false);

        var different = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGP4DwABAQEAsTj2FAAAAABJRU5ErkJggg==");
        await File.WriteAllBytesAsync(fixture.PathFor("frame.png"), different, Token).ConfigureAwait(false);
        fixture.Select([first with { ImageSha256 = SiloHash.Sha256(different) }]);
        var secondInventory = await InventoryAsync(source).ConfigureAwait(false);
        using var otherSilo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var otherImport = await PublishAsync(new(source, secondInventory), otherSilo).ConfigureAwait(false);

        var firstRow = imported.Records.Single();
        var secondRow = otherImport.Records.Single();
        firstRow.Status.Should().Be("imported");
        secondRow.Status.Should().Be("imported");
        secondRow.ArtifactId.Should().NotBe(firstRow.ArtifactId);
        secondRow.GroupId.Should().NotBe(firstRow.GroupId);
        silo.PublishRecord(firstRow.GroupId!, "1").GroupKind.Should().Be("import");
        otherSilo.PublishRecord(secondRow.GroupId!, "1").GroupKind.Should().Be("import");
    }

    [TestMethod]
    public async Task DryRunReportsTargetAgeVerdictUnavailableForEachActualRetentionClass()
    {
        using var fixture = new Fixture();
        var entry = fixture.WritePair("frame", "art-1", daysOld: 45);
        using var source = fixture.Open();
        fixture.Select([entry], displayRetentionDays: 30);
        var first = await InventoryAsync(source).ConfigureAwait(false);
        fixture.Select([entry], displayRetentionDays: 730);
        var second = await InventoryAsync(source).ConfigureAwait(false);
        var firstReport = await new SiloFixtureImport(source, first).RunAsync(SiloFixtureMode.DryRun,
            () => new(Reserve * 2, Reserve * 2), null, Token).ConfigureAwait(false);
        var secondReport = await new SiloFixtureImport(source, second).RunAsync(SiloFixtureMode.DryRun,
            () => new(Reserve * 2, Reserve * 2), null, Token).ConfigureAwait(false);

        var objects = firstReport.Records.Single().Objects!;
        objects.Should().BeEquivalentTo(secondReport.Records.Single().Objects);
        objects.Select(static item => item.RetentionClass).Should().Equal(SiloRetentionClass.Display, SiloRetentionClass.Metadata);
        objects.Should().OnlyContain(static item => item.ImmediatelyAgeEligible == null &&
            item.AgeEligibilityReason == "target-retention-policy-unavailable");
    }

    [TestMethod]
    public async Task RetryingWithTightHeadroomOnlyChargesThePendingPair()
    {
        const long mib = 1024 * 1024;
        using var fixture = new Fixture();
        var first = fixture.WritePair("first", "art-1");
        var second = fixture.WritePair("second", "art-2");
        using var bitmap = new SKBitmap(640, 640);
        var pixels = RandomNumberGenerator.GetBytes(bitmap.Width * bitmap.Height * 3);
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var index = (y * bitmap.Width + x) * 3;
                bitmap.SetPixel(x, y, new SKColor(pixels[index], pixels[index + 1], pixels[index + 2]));
            }
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = encoded.ToArray();
        image.LongLength.Should().BeGreaterThan(mib);
        await File.WriteAllBytesAsync(fixture.PathFor("first.png"), image, Token).ConfigureAwait(false);
        first = first with { ImageSha256 = SiloHash.Sha256(image) };
        fixture.Select([first, second], maximumBytes: 4 * mib, minimumFreeBytes: mib);
        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var firstOnly = await PublishAsync(new(source, inventory with { Records = [inventory.Records[0]] }), silo).ConfigureAwait(false);
        firstOnly.Records.Single().Status.Should().Be("imported");
        await silo.RestartAsync().ConfigureAwait(false);

        var retry = await PublishAsync(new(source, inventory), silo, () => new(3 * mib, 3 * mib)).ConfigureAwait(false);

        retry.ExitCode.Should().Be(0);
        retry.Records[0].Reason.Should().Be("already-imported");
        retry.Records[1].Status.Should().Be("imported");
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().HaveCount(2);
    }

    [TestMethod]
    public async Task CapacityProbeFailureKeepsCommittedAndDeferredRowsInTheManifest()
    {
        using var fixture = new Fixture();
        fixture.Select([fixture.WritePair("first", "art-1"), fixture.WritePair("second", "art-2"), fixture.WritePair("third", "art-3")]);
        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var probes = 0;

        var report = await PublishAsync(new(source, inventory), silo, () => ++probes == 1
            ? new(Reserve * 2, Reserve * 2) : throw new IOException("injected capacity failure")).ConfigureAwait(false);

        report.ExitCode.Should().Be(2);
        report.Records.Select(static row => row.Stem).Should().Equal("first", "second", "third");
        report.Records.Select(static row => row.Status).Should().Equal("imported", "failed", "skipped");
        report.Records.Select(static row => row.Reason).Should().Equal(null, "capacity-probe-unavailable", "deferred-after-failure");
        silo.JournalRecords().OfType<SiloPublishRecord>().Should().ContainSingle();
    }

    private static ValueTask<SiloFixtureInventory> InventoryAsync(ConfinedReadDirectory source) =>
        SiloFixtureInventory.ReadAsync(source, SelectionFile, Now, Token);

    private static Task<SiloFixtureReport> PublishAsync(SiloFixtureImport import, SiloHarness silo,
        Func<SiloFixtureCapacity>? readCapacity = null) =>
        import.RunAsync(SiloFixtureMode.Publish, readCapacity ?? (() => new(Reserve * 2, Reserve * 2)),
            (draft, open, requireHeadroom, token) => SiloFixtureImport.PublishPairAsync(silo.State, silo.Publisher, silo.Catalog,
                draft, open, requireHeadroom, token), Token).AsTask();

    private static async Task AssertInventoryRecordAsync(Fixture fixture, string status, string reason)
    {
        using var source = fixture.Open();
        var inventory = await InventoryAsync(source).ConfigureAwait(false);
        var record = inventory.Records.Single();
        record.Status.Should().Be(status);
        record.Reason.Should().Be(reason);
    }

    private static Task AssertInventoryRefusalAsync(Fixture fixture, string reason) => AssertCommandRefusalAsync(
        [SiloFixtureImportCommand.Argument, "inventory", fixture.Root, SelectionFile],
        static () => throw new InvalidOperationException("Inventory must not load target configuration."), reason);

    private static async Task AssertCommandRefusalAsync(string[] args, Func<IConfiguration> readConfiguration, string reason)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await SiloFixtureImportCommand.RunAsync(args, readConfiguration, output, Token).ConfigureAwait(false);
        exit.Should().Be(2, output.ToString());
        using var report = JsonDocument.Parse(output.ToString());
        report.RootElement.GetProperty("status").GetString().Should().Be("refused");
        report.RootElement.GetProperty("reason").GetString().Should().Be(reason);
    }

    private static Dictionary<string, string?> TargetSettings(string stateRoot, out string bucket, Guid? pause = null)
    {
        var instance = Guid.NewGuid();
        bucket = CameraAgentSiloBinding.BucketFor(instance);
        var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance), bucket);
        return new(StringComparer.Ordinal)
        {
            ["CameraAgent:RawIngressRoot"] = stateRoot,
            ["CameraAgent:FirstBootPause:Id"] = pause?.ToString("D"),
            ["CameraAgent:Silo:StateContract"] = CameraAgentSiloBinding.StateContract,
            ["CameraAgent:Silo:StoreId"] = binding.StoreId,
            ["CameraAgent:Silo:Bucket"] = bucket,
            ["CameraAgent:Silo:StorageBindingId"] = binding.BindingId,
            ["CameraAgent:Silo:WriterGeneration"] = "1",
            ["CameraAgent:Silo:Endpoint"] = "http://127.0.0.1:1/",
            ["CameraAgent:Silo:AccessKey"] = "fixtureAccessKey1234",
            ["CameraAgent:Silo:SecretKey"] = "fixtureSecretKey1234"
        };
    }

    private static Dictionary<string, string> SnapshotTree(string root) =>
        new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToDictionary(
            entry => Path.GetRelativePath(root, entry.FullName), DescribeEntry, StringComparer.Ordinal);

    private static string DescribeEntry(FileSystemInfo entry)
    {
        if (entry.LinkTarget is { } target)
            return $"link:{target}";
        if (entry is not FileInfo file)
            return "directory";
        if (file.Name == SiloStateRoot.LockFileName)
            return $"lock:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        // Read transactions can update transient SQLite read marks. Compare the database and WAL bytes below.
        if (file.Name.EndsWith("-shm", StringComparison.Ordinal))
            return "sqlite-shared-memory";
        return $"file:{file.LastWriteTimeUtc.Ticks}:{SiloHash.Sha256(File.ReadAllBytes(file.FullName))}";
    }

    private static void Resize(string path, int length)
    {
        using var file = File.OpenWrite(path);
        file.SetLength(length);
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("silo-history-fixture");

        public string PathFor(string leaf) => Path.Combine(Root, leaf);

        public ConfinedReadDirectory Open() => new(Root);

        public SiloFixtureSelectionEntry WritePair(string stem, string artifactId, int daysOld = 1,
            SiloFixtureSourceDependency? dependency = null, string format = SiloFixtureInventory.SidecarFormat)
        {
            var end = Now.AddDays(-daysOld);
            return WritePair(stem, new SiloFixtureSidecar(format, "capture-" + stem, "camera", artifactId,
                end.AddSeconds(-1), end, dependency));
        }

        public SiloFixtureSelectionEntry WritePair(string stem, SiloFixtureSidecar facts)
        {
            var sidecar = SiloJson.Serialize(facts);
            File.WriteAllBytes(PathFor(stem + ".png"), Png);
            File.WriteAllBytes(PathFor(stem + ".json"), sidecar);
            return new(stem, SiloHash.Sha256(Png), SiloHash.Sha256(sidecar));
        }

        public string Select(SiloFixtureSelectionEntry[] entries, long maximumBytes = 1024 * 1024,
            int? displayRetentionDays = null, long minimumFreeBytes = Reserve)
        {
            var selection = SiloJson.Serialize(
                new SiloFixtureSelection(SiloFixtureInventory.SelectionFormat, "sanitized-export", maximumBytes, minimumFreeBytes,
                    entries, displayRetentionDays));
            File.WriteAllBytes(PathFor(SelectionFile), selection);
            return SiloHash.Sha256(selection);
        }

        public Dictionary<string, (string Sha, long Length, DateTime Changed)> Snapshot() =>
            Directory.EnumerateFiles(Root).ToDictionary(static path => Path.GetFileName(path), static path =>
            {
                var bytes = File.ReadAllBytes(path);
                return (SiloHash.Sha256(bytes), bytes.LongLength, File.GetLastWriteTimeUtc(path));
            }, StringComparer.Ordinal);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
