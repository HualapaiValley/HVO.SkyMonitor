using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
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
    public async Task OverlappingCatchUpAcceptsTheSameSegmentButRefusesAnotherHash()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var silo = await SiloHarness.CreateAsync(install: false).ConfigureAwait(false);
        await silo.Journal.OpenFirstGenerationAsync(new Dictionary<string, long> { ["capture-sequence"] = 0 }, token).ConfigureAwait(false);
        var writer = await silo.WriterEvidenceAsync().ConfigureAwait(false);
        var bothWalking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        silo.Local.BeforeList = async cancellationToken =>
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothWalking.TrySetResult();
            }
            await bothWalking.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };

        var ends = await Task.WhenAll(
            silo.Catalog.CatchUpAsync(writer, null, token).AsTask(),
            silo.Catalog.CatchUpAsync(writer, null, token).AsTask()).ConfigureAwait(false);
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
    public async Task RegisteredStackStaysHiddenUntilRestartCommitsAllRequiredFiles()
    {
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
        await File.WriteAllBytesAsync(partial, [1, 2, 3], token).ConfigureAwait(false);
        await silo.RestartAsync().ConfigureAwait(false);
        Assert.IsFalse(File.Exists(partial));
        var resumed = await silo.Publisher.ResumeAsync(1, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, resumed.Single().State);
        var reopened = await silo.Catalog.ReopenAsync(staged.GroupId, staged.RevisionId, token).ConfigureAwait(false);
        Assert.IsTrue(reopened.Reopenable);
        Assert.IsTrue(reopened.Items.Where(static item => item.Required)
            .All(item => item.Disposition == SiloDisposition.Available && item.ServedFrom?.StoreId == silo.Local.Descriptor.StoreId));
        Assert.AreEqual(0, Directory.GetFiles(payloadDirectory).Length);
    }
}
