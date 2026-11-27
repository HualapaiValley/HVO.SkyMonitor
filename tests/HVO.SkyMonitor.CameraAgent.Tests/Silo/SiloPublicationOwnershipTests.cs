using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestProperty("Area", "Silo")]
public sealed class SiloPublicationOwnershipTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Unit")]
    public void InstalledSiloSettingsRejectCentralIntegration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:Silo:StateContract"] = "cameraagent-silo-state-v1",
            ["CameraAgent:CentralIntegration:Mode"] = "Enabled"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        var failure = Assert.ThrowsExactly<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value);
        CollectionAssert.Contains(failure.Failures.ToArray(), "The Silo storage layout requires disabled central integration.");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstalledSiloSettingsRejectLocalRunnerReplay()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:Silo:StateContract"] = "cameraagent-silo-state-v1",
            ["CameraAgent:ProcessingGraphs:ReplayProfile"] = "LocalRunner"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        var failure = Assert.ThrowsExactly<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value);
        CollectionAssert.Contains(failure.Failures.ToArray(), "The Silo storage layout requires in-process replay.");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OverlappingCatchUpAcceptsTheSameSegmentButRefusesAnotherHash()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var silo = await SiloHarness.CreateAsync(install: false).ConfigureAwait(false);
        await silo.Journal.OpenFirstGenerationAsync(new Dictionary<string, long> { ["capture-sequence"] = 0 }, token).ConfigureAwait(false);
        var firstSegment = await silo.WriterEvidenceAsync().ConfigureAwait(false);
        await silo.Journal.AppendAsync(
            [new SiloLiveIssueRecord("camera", "stream", SiloDrafts.CaptureGroup(1), "r1", 1, SiloHarness.Start)],
            "second-segment", token).ConfigureAwait(false);
        var writer = await silo.WriterEvidenceAsync().ConfigureAwait(false);
        Assert.AreEqual(new SiloJournalPosition(1, 2), writer.Head);

        var firstWalking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeWalk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        silo.Local.BeforeList = async cancellationToken =>
        {
            firstWalking.TrySetResult();
            await resumeWalk.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
        var first = silo.Catalog.CatchUpAsync(writer, null, token).AsTask();
        await firstWalking.Task.WaitAsync(token).ConfigureAwait(false);
        var second = silo.Catalog.CatchUpAsync(writer, null, token).AsTask();
        Assert.IsFalse(second.IsCompleted);
        resumeWalk.TrySetResult();
        var ends = await Task.WhenAll(first, second).ConfigureAwait(false);
        var superseded = await silo.Catalog.CatchUpAsync(firstSegment, null, token).ConfigureAwait(false);
        Assert.AreEqual(writer.Head, superseded.Last);
        foreach (var end in ends)
        {
            Assert.AreEqual(SiloPrefixEnd.Tail, end.Reason);
            Assert.AreEqual(writer.Head, end.Last);
            Assert.AreEqual(writer.HeadSha256, end.LastSha256);
        }

        var position = writer.Head!.Value;
        await Assert.ThrowsExactlyAsync<SiloCatalogDivergedException>(async () =>
            await silo.Index.ApplySegmentAsync(position, SiloHash.Sha256("different bytes"u8), null, [],
                generationWitnessed: true, token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual((writer.Head, writer.HeadSha256), await silo.Index.ReadCursorAsync(token).ConfigureAwait(false));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OneVisibleGroupCannotReleaseAnotherGroupsStagedBytes()
    {
        if (!DurableSync.SupportsDirectorySync)
            Assert.Inconclusive("The durable payload spool requires Linux directory fsync.");
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Evidence(silo.Payloads, "shared");
        var member = draft.Members.Single();
        var sourceToken = SiloFilePayloadSpool.TokenOf(member.Sha256);
        var first = draft with { Members = [member with { SourceToken = sourceToken }] };
        var second = first with { GroupId = "second-group" };
        silo.FileSpoolCapacity = (member.Length, 1);
        await silo.RestartAsync().ConfigureAwait(false);

        ValueTask<Stream> OpenContent(SiloDraftMember _, CancellationToken ct) => silo.Payloads.OpenAsync(member.SourceToken, ct);
        await silo.Publisher.StageAsync(first, OpenContent, token).ConfigureAwait(false);
        await silo.Publisher.StageAsync(second, OpenContent, token).ConfigureAwait(false);

        var result = await silo.Publisher.PublishAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, result.State);
        await silo.RestartAsync().ConfigureAwait(false);
        var staged = await silo.PayloadSpool.OpenAsync(sourceToken, token).ConfigureAwait(false);
        await using (staged.ConfigureAwait(false))
        {
            var digest = await SHA256.HashDataAsync(staged, token).ConfigureAwait(false);
            Assert.AreEqual(first.Members.Single().Sha256, Convert.ToHexStringLower(digest));
        }
        var resumed = await silo.Publisher.ResumeAsync(1, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, resumed.Single().State);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
            await silo.PayloadSpool.OpenAsync(sourceToken, token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task StagedRawCaptureDrainsAfterRestartWithLowerSpoolLimit()
    {
        if (!DurableSync.SupportsDirectorySync)
            Assert.Inconclusive("The durable payload spool requires Linux directory fsync.");
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var capture = SiloDrafts.Capture(silo.Payloads, 1);
        var evidence = SiloDrafts.Member(silo.Payloads, capture.Members[0].ArtifactId, "raw-evidence",
            SiloRetentionClass.Metadata, "application/json", "{}"u8.ToArray(), revision: SiloArtifactStorage.RawEvidenceRevision);
        SiloDraftMember[] members = [capture.Members[0], evidence];
        var draft = capture with
        {
            GroupKind = "raw-capture",
            Members = members.Select(static member => member with { SourceToken = SiloFilePayloadSpool.TokenOf(member.Sha256) }).ToArray()
        };
        var sourceTokens = members.ToDictionary(static member => member.Sha256, static member => member.SourceToken);
        var stagedBytes = members.Sum(static member => member.Length);
        silo.FileSpoolCapacity = (stagedBytes, members.Length);
        await silo.RestartAsync().ConfigureAwait(false);
        ValueTask<Stream> OpenContent(SiloDraftMember member, CancellationToken ct) => silo.Payloads.OpenAsync(sourceTokens[member.Sha256], ct);
        Assert.IsTrue(await silo.Publisher.StageAsync(draft, OpenContent, token).ConfigureAwait(false));

        silo.FileSpoolCapacity = (stagedBytes - 1, members.Length);
        await silo.RestartAsync().ConfigureAwait(false);
        var payloadDirectory = new DirectoryInfo(Path.Combine(silo.RootPath, "payloads-v1"));
        Assert.AreEqual(stagedBytes, payloadDirectory.GetFiles("*.payload").Sum(static file => file.Length));
        Assert.ThrowsExactly<SiloSpoolFullException>(silo.PayloadSpool.RequireWithinCapacity);
        Assert.AreEqual(0, (await silo.Publisher.ResumeAsync(1, skipRawAdmissions: true, token).ConfigureAwait(false)).Count);
        Assert.AreEqual(SiloStagedState.Staged,
            (await silo.State.ReadGroupAsync(draft.GroupId, draft.RevisionId, token).ConfigureAwait(false))?.State);

        Assert.IsFalse(await silo.Publisher.StageAsync(draft, OpenContent, token).ConfigureAwait(false));
        var result = await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, result.State);
        Assert.IsTrue((await silo.Catalog.ReopenAsync(draft.GroupId, draft.RevisionId, token).ConfigureAwait(false)).Reopenable);
        Assert.AreEqual(0, payloadDirectory.GetFiles().Length);
        silo.PayloadSpool.RequireWithinCapacity();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RegisteredStackStaysHiddenUntilRestartCommitsAllRequiredFiles()
    {
        if (!DurableSync.SupportsDirectorySync)
            Assert.Inconclusive("The durable payload spool requires Linux directory fsync.");
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.View(silo.Payloads, "registered", null, 1, [], [], publishPreview: true);
        var sourceTokens = draft.Members.ToDictionary(static member => member.Sha256, static member => member.SourceToken);
        var staged = draft with
        {
            Members = draft.Members.Select(static member => member with { SourceToken = SiloFilePayloadSpool.TokenOf(member.Sha256) }).ToArray()
        };
        silo.FileSpoolCapacity = (draft.Members.Sum(static member => member.Length), draft.Members.Count);
        await silo.RestartAsync().ConfigureAwait(false);
        ValueTask<Stream> OpenContent(SiloDraftMember member, CancellationToken ct) => silo.Payloads.OpenAsync(sourceTokens[member.Sha256], ct);
        await silo.Publisher.StageAsync(staged, OpenContent, token).ConfigureAwait(false);

        var extra = SiloDrafts.Evidence(silo.Payloads, "outage-pressure").Members.Single();
        await Assert.ThrowsExactlyAsync<SiloSpoolFullException>(async () =>
            await silo.PayloadSpool.StageAsync(extra with { SourceToken = SiloFilePayloadSpool.TokenOf(extra.Sha256) },
                (_, ct) => silo.Payloads.OpenAsync(extra.SourceToken, ct), token).ConfigureAwait(false)).ConfigureAwait(false);

        silo.Faults.Armed = SiloFaultPoint.AfterManifestQualified;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await silo.Publisher.PublishAsync(staged.GroupId, staged.RevisionId, token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsFalse((await silo.Catalog.ReopenAsync(staged.GroupId, staged.RevisionId, token).ConfigureAwait(false)).Reopenable);

        var payloadDirectory = Path.Combine(silo.RootPath, "payloads-v1");
        var partial = Path.Combine(payloadDirectory, $"{SiloFilePayloadSpool.TokenOf(extra.Sha256)}.{Guid.NewGuid():N}.tmp");
        var metadataPartial = Path.Combine(silo.RootPath, SiloStateRoot.SpoolDirectoryName,
            $"{SiloObjectSpool.NameOf(extra.Sha256)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(partial, [1, 2, 3], token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(metadataPartial, [1, 2, 3], token).ConfigureAwait(false);
        await silo.RestartAsync().ConfigureAwait(false);
        Assert.IsFalse(File.Exists(partial));
        Assert.IsFalse(File.Exists(metadataPartial));
        var resumed = await silo.Publisher.ResumeAsync(1, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, resumed.Single().State);
        var reopened = await silo.Catalog.ReopenAsync(staged.GroupId, staged.RevisionId, token).ConfigureAwait(false);
        Assert.IsTrue(reopened.Reopenable);
        Assert.IsTrue(reopened.Items.Where(static item => item.Required)
            .All(item => item.Disposition == SiloDisposition.Available && item.ServedFrom?.StoreId == silo.Local.Descriptor.StoreId));
        Assert.AreEqual(0, Directory.GetFiles(payloadDirectory).Length);
    }
}
