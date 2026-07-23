using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.DataProtection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Operations;

[TestClass]
[TestCategory("Unit")]
public sealed class OutboxOperationsTokenServiceTests
{
    [TestMethod]
    public void ProtectedReferencesAndActions_ArePurposeSeparated()
    {
        var tokens = new OutboxOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(1));
        var reference = tokens.ProtectArtifactReference("raw-ingress", "private-idempotency-key");
        var replay = tokens.ProtectArtifactAction(
            OutboxOperationAction.Replay, "raw-ingress", "private-idempotency-key");

        Assert.IsTrue(tokens.TryReadArtifactReference(reference, out var alias, out var key));
        Assert.AreEqual("raw-ingress", alias);
        Assert.AreEqual("private-idempotency-key", key);
        Assert.IsFalse(tokens.TryReadEnvironmentalReference(reference, out _));
        Assert.IsFalse(tokens.TryReadArtifactAction(OutboxOperationAction.Abandon, replay, out _, out _));
        Assert.IsFalse(reference.Contains("private-idempotency-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProtectedCursor_Expires()
    {
        var tokens = new OutboxOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMilliseconds(1));
        var cursor = tokens.ProtectEnvironmentalCursor(new(42));

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.IsFalse(tokens.TryReadEnvironmentalCursor(cursor, out _));
    }

    [TestMethod]
    public void QuarantineCursorsRejectTamperCrossKindAndCrossAlias()
    {
        var tokens = new OutboxOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(1));
        var artifact = tokens.ProtectArtifactCursor("storage-1", new(42));
        var environmental = tokens.ProtectEnvironmentalCursor(new(42));

        Assert.IsTrue(tokens.TryReadArtifactCursor(artifact, "storage-1", out var parsed));
        Assert.AreEqual(42, parsed!.RecordId);
        Assert.IsFalse(tokens.TryReadArtifactCursor(artifact + "x", "storage-1", out _));
        Assert.IsFalse(tokens.TryReadArtifactCursor(artifact, "storage-2", out _));
        Assert.IsFalse(tokens.TryReadEnvironmentalCursor(artifact, out _));
        Assert.IsFalse(tokens.TryReadArtifactCursor(environmental, "raw-ingress", out _));
        Assert.AreNotEqual("42", artifact);
    }
}
