using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearedLedgerCannotMasqueradeAsLegacyState(bool changeEvidence)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        store.Dispose();
        var historyPath = HistoryPath();
        var history = await File.ReadAllBytesAsync(historyPath).ConfigureAwait(false);
        DeleteFixtureLedger();
        if (changeEvidence) await File.WriteAllBytesAsync(path, [.. bytes, (byte)' ']).ConfigureAwait(false);
        using var restarted = CreateStore();
        Assert.AreEqual(active, await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false),
            "Recovery remains available without adopting cleared required state.");
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        CollectionAssert.AreEqual(history, await File.ReadAllBytesAsync(historyPath).ConfigureAwait(false));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task GenuineLegacyHistoryIsSealedWithoutChangingInstallationIdentity()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        store.Dispose();
        var legacy = await ReadHistoryObjectAsync().ConfigureAwait(false);
        Assert.IsTrue(legacy.Remove("retainedIntegrityNonce"));
        await WriteHistoryObjectAsync(legacy).ConfigureAwait(false);
        DeleteFixtureLedger();
        using var upgraded = CreateStore();
        Assert.AreEqual(active, await upgraded.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        var sealedHistory = await ReadHistoryObjectAsync().ConfigureAwait(false);
        Assert.AreEqual(32, sealedHistory["retainedIntegrityNonce"]!.GetValue<string>().Length);
        Assert.IsTrue(sealedHistory.Remove("retainedIntegrityNonce"));
        Assert.IsTrue(JsonNode.DeepEquals(legacy, sealedHistory));
        Assert.AreEqual(active, await upgraded.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        upgraded.Dispose();
        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InterruptedAdoptionRecoversThroughFullScrubWithTheOriginalNonce()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        store.BeforeStateDirectorySyncForTest = _ =>
        {
            if (File.Exists(HistoryPath())) throw new IOException("Injected adoption interruption.");
        };
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        store.Dispose();
        var interrupted = await ReadHistoryObjectAsync().ConfigureAwait(false);
        var nonce = interrupted["retainedIntegrityNonce"]!.GetValue<string>();
        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        _ = await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(nonce, (await ReadHistoryObjectAsync().ConfigureAwait(false))["retainedIntegrityNonce"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ProtectedHistoryCannotQualifyWithAnotherLedgersNonce()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        store.Dispose();
        var wrongPair = await ReadHistoryObjectAsync().ConfigureAwait(false);
        wrongPair["retainedIntegrityNonce"] = Guid.NewGuid().ToString("N");
        await WriteHistoryObjectAsync(wrongPair).ConfigureAwait(false);
        using var restarted = CreateStore();
        Assert.AreEqual(active, await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private string HistoryPath() => Path.Combine(_options.Value.RawIngressRoot, ".location", "deployment-location.v1.protected");
    private async Task<JsonObject> ReadHistoryObjectAsync()
        => JsonNode.Parse(_protector.Unprotect(await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false)))!.AsObject();
    private async Task WriteHistoryObjectAsync(JsonObject history)
        => await File.WriteAllBytesAsync(HistoryPath(), _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(history))).ConfigureAwait(false);
    private void DeleteFixtureLedger()
    {
        foreach (var name in new[] { RetainedIntegrityLedger.DatabaseName, RetainedIntegrityLedger.CheckpointName })
            File.Delete(Path.Combine(_options.Value.RawIngressRoot, ".location", name));
    }
}
