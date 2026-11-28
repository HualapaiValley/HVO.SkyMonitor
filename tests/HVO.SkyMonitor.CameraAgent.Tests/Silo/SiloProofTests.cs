using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// An exact read proves one object's bytes: the exact version together with the length and SHA-256 that object must
/// have (#1965 4.2, 5.3, 6.1). Within one operation a verification is reused only for that same exact expectation, so a
/// manifest or another member read through the same version never stands in for a different role.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloProofTests
{
    [TestMethod]
    public void AVerificationIsReusedOnlyForTheSameExactVersionLengthAndDigest()
    {
        var fresh = new SiloFreshEvidence();
        var locator = new SiloExactLocator("local-silo", "instance-bucket", "p/full/ab/" + SiloHash.Sha256("a"u8), "v-1");
        var sha = SiloHash.Sha256("a"u8);
        fresh.Verified(locator, 1, sha, SiloHarness.Start, [(byte)'a']);

        fresh.TryGet(locator, 1, sha, out _, out var bytes).Should().BeTrue();
        bytes.Should().Equal((byte)'a');
        fresh.TryGet(locator, 1, SiloHash.Sha256("b"u8), out _, out _).Should().BeFalse("another digest is another object");
        fresh.TryGet(locator, 2, sha, out _, out _).Should().BeFalse("another length is another object");
        fresh.TryGet(locator with { VersionId = "v-2" }, 1, sha, out _, out _).Should().BeFalse("another version proves nothing");
    }

    [TestMethod]
    [DataRow("member", DisplayName = "a copy receipt names another required member's exact version")]
    [DataRow("manifest", DisplayName = "a copy receipt names the revision's own manifest version")]
    public async Task AReadOfOneObjectNeverVerifiesAnotherRoleThroughTheSameVersion(string named)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var capture = silo.PublishRecord(SiloDrafts.CaptureGroup(1));
        var raw = SiloHarness.Identity(capture, SiloDrafts.Raw(1));
        var preview = SiloHarness.Identity(capture, SiloDrafts.Preview(1));
        // The holder is read first in the same reopen; the receipt binds the target's identity to the holder's version.
        var (target, holder) = named == "member"
            ? (preview, silo.MemberLocator(capture.GroupId, raw.Id))
            : (raw, capture.Manifest.Locator);
        await silo.AppendAsync(new SiloCopyRecord(target, holder, SiloStoreClass.Local, SiloRetrieval.Online, "replica",
            silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256)).ConfigureAwait(false);
        silo.Local.Delete(silo.MemberLocator(capture.GroupId, target.Id));

        var reopened = await silo.Catalog.ReopenAsync(capture.GroupId, "r1", CancellationToken.None).ConfigureAwait(false);

        reopened.Reopenable.Should().BeFalse("the target role's own bytes exist nowhere");
        var item = reopened.Items.Single(item => item.Role.Id == target.Id);
        item.ServedFrom.Should().BeNull("another object's bytes never serve this role");
        item.Disposition.Should().NotBe(SiloDisposition.Available);
        reopened.Items.Should().Contain(other => other.ServedFrom == holder, "the holder itself still verifies as its own role");
    }
}
