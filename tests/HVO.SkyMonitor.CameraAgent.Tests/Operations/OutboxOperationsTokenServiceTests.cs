using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
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

    [TestMethod]
    public void TransientRuntimeTokens_BindExactImmutableIdentity()
    {
        var tokens = new OutboxOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(1));
        var updated = new DateTimeOffset(2026, 8, 22, 1, 2, 3, TimeSpan.Zero);
        var target = new TransientRuntimeOperationTarget(
            42, 43, 44, "agent-east", 10, Guid.NewGuid(), Guid.NewGuid(),
            new string('A', 64), new string('C', 64), new string('B', 64), "hybrid", true,
            "completed", "quarantined", "quarantined", "transient-runtime.input-levels-invalid",
            updated.AddSeconds(-2), updated.AddSeconds(-1), updated);
        var reference = tokens.ProtectTransientRuntimeReference(target);
        var bound = target with
        {
            ExternalOwnershipEvidence = new("d331-0821084607", new string('D', 64), true)
        };
        var action = tokens.ProtectTransientRuntimeAction(bound);
        var cursor = tokens.ProtectTransientRuntimeCursor(new(updated.ToUnixTimeMilliseconds(), 42));

        Assert.IsTrue(tokens.TryReadTransientRuntimeAction(action, out var parsed));
        Assert.AreEqual(bound, parsed);
        Assert.IsTrue(tokens.TryReadTransientRuntimeReference(reference, out var parsedReference));
        Assert.AreEqual(target, parsedReference);
        Assert.IsFalse(tokens.TryReadTransientRuntimeAction(reference, out _));
        Assert.IsFalse(tokens.TryReadTransientRuntimeReference(action, out _));
        Assert.IsFalse(tokens.TryReadArtifactAction(OutboxOperationAction.Abandon, action, out _, out _));
        Assert.IsTrue(tokens.TryReadTransientRuntimeCursor(cursor, out var parsedCursor));
        Assert.AreEqual(42, parsedCursor!.RawCaptureRowId);
        Assert.AreEqual(updated.ToUnixTimeMilliseconds(), parsedCursor.UpdatedUnixMs);
        Assert.IsFalse(tokens.TryReadTransientRuntimeCursor(cursor + "x", out _));
        Assert.IsFalse(action.Contains(target.AgentId, StringComparison.Ordinal));
    }

    [TestMethod]
    public void MalformedActionEnum_DoesNotFallBackToAbandon()
    {
        var tokens = new OutboxOperationsTokenService(
            new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(1));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            tokens.ProtectArtifactAction((OutboxOperationAction)999, "raw-ingress", "record"));
        Assert.IsFalse(OutboxOperationsReasonCodes.IsAllowed(
            (OutboxOperationAction)999, "operator-approved-loss"));
    }
}
